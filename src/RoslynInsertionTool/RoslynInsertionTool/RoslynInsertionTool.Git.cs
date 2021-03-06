// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace Roslyn.Insertion
{
    static partial class RoslynInsertionTool
    {
        private const string RefsHeadsPrefix = "refs/heads/";
        private const string RefsRemotesPrefix = "refs/remotes/";

        private static readonly Lazy<Repository> LazyEnlistment = new Lazy<Repository>(() =>
        {
            var absolutePath = GetAbsolutePathForEnlistment();
            Console.WriteLine($"Creating git repository object for {absolutePath}");
            return new Repository(absolutePath);
        });

        private static readonly Lazy<Signature> LazyInsertionToolSignature = new Lazy<Signature>(() => new Signature("Roslyn Insertion Tool", Options.Username, DateTimeOffset.Now));

        private static Repository Enlistment => LazyEnlistment.Value;

        private static Signature InsertionToolSignature => LazyInsertionToolSignature.Value;

        private static Branch CheckoutBranch(Repository enlistment, string branchName, Branch newlocalBranch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stopwatch watch;
            Console.WriteLine($"Checking out branch {branchName}");
            watch = Stopwatch.StartNew();
            newlocalBranch = enlistment.Checkout(newlocalBranch, GetCheckoutOptions());
            Console.WriteLine($"Checking out branch took {watch.Elapsed.TotalSeconds} seconds");

            if (newlocalBranch.IsCurrentRepositoryHead)
            {
                Console.WriteLine($"{branchName} is the current repository head.");
            }
            else
            {
                Console.WriteLine($"{branchName} is NOT the current repository head.");
            }

            return newlocalBranch;
        }

        private static void CommitStagedChanges(BuildVersion newRoslynVersion, CancellationToken cancellationToken)
        {
            var message = $"Updating {Options.InsertionName} to {newRoslynVersion}";

            Stopwatch watch;
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Committing file(s)");
            watch = Stopwatch.StartNew();
            var commit = Enlistment.Commit(
                message, InsertionToolSignature, InsertionToolSignature);
            Console.WriteLine($"Committing took {watch.Elapsed.TotalSeconds} seconds");
        }

        private static Branch CreateBranch(Repository enlistment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Create branch and remote tracking branch
            CreateNewBranch(enlistment, cancellationToken, out string branchName, out var newLocalBranch);

            // Checkout Branch
            newLocalBranch = CheckoutBranch(enlistment, branchName, newLocalBranch, cancellationToken);

            return newLocalBranch;
        }

        private static void CreateNewBranch(Repository enlistment, CancellationToken cancellationToken, out string branchName, out Branch newlocalBranch)
        {
            Stopwatch watch;
            cancellationToken.ThrowIfCancellationRequested();
            branchName = GetNewBranchName();
            Console.WriteLine($"Creating new branch {branchName}");
            var remoteTrackingBranchName = "origin/" + Options.VisualStudioBranchName;
            var remoteTrackingBranch = enlistment.Branches[remoteTrackingBranchName];
            watch = Stopwatch.StartNew();
            newlocalBranch = enlistment.CreateBranch(branchName, remoteTrackingBranch.Tip);
            newlocalBranch = enlistment.Branches.Update(newlocalBranch, b => { b.Remote = "origin"; b.UpstreamBranch = Options.VisualStudioBranchName; });
            Console.WriteLine($"Creating branch took {watch.Elapsed.TotalSeconds} seconds");
        }

        private static string GetNewBranchName() => $"{Options.NewBranchName}{Options.VisualStudioBranchName.Split('/').Last()}.{DateTime.Now:yyyyMMddHHmmss}";

        private static string GetAbsolutePathForEnlistment() => Path.GetFullPath(Options.EnlistmentPath);

        private static CheckoutOptions GetCheckoutOptions()
        {
            Console.WriteLine("Getting checkout options");
            return new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force,
                CheckoutNotifyFlags = CheckoutNotifyFlags.Conflict,
            };
        }

        private static FetchOptions GetFetchOptions()
        {
            Console.WriteLine("Getting fetch options");
            return new FetchOptions()
            {
                CredentialsProvider = new LibGit2Sharp.Handlers.CredentialsHandler((_, __, ___) => GetCredentials()),
                TagFetchMode = TagFetchMode.None
            };
        }

        private static Credentials GetCredentials()
        {
            Console.WriteLine("Getting credentials");
            return new UsernamePasswordCredentials
            {
                Username = Options.Username,
                Password = Options.Password
            };
        }

        private static Branch CreateBranch(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Loading git enlistment at {Options.EnlistmentPath}");
            var enlistment = Enlistment;
            return CreateBranch(enlistment, cancellationToken);
        }

        private static PushOptions GetPushOptions()
        {
            Console.WriteLine("Getting push options");
            return new PushOptions
            {
                CredentialsProvider = (url, usernameFromUrl, types) => GetCredentials()
            };
        }

        private static StageOptions GetStageOptions()
        {
            return new StageOptions
            {
                ExplicitPathsOptions = new ExplicitPathsOptions
                {
                    OnUnmatchedPath = (s) => Console.WriteLine($"Path could not be matched '{s}'"),
                    ShouldFailOnUnmatchedPath = true
                },
                IncludeIgnored = true
            };
        }

        private static Branch SwitchToBranchAndUpdate(string branchToSwitchTo, string baseBranchName, bool overwriteExistingChanges)
        {
            if (branchToSwitchTo.StartsWith(RefsHeadsPrefix))
            {
                branchToSwitchTo = branchToSwitchTo.Substring(RefsHeadsPrefix.Length);
            }

            var baselineBranchName = overwriteExistingChanges ? baseBranchName : branchToSwitchTo;

            Console.WriteLine($"Switching to branch {branchToSwitchTo} resetting it to {baselineBranchName}");

            var destinationBranch = Enlistment.Branches[branchToSwitchTo];
            if (destinationBranch == null)
            {
                // Branch might not exist locally if it was originally created on another machine.  The workaround is
                // simply to make sure a branch with that name exists; contents don't matter since it's going to be
                // overwritten anyways.
                Enlistment.CreateBranch(branchToSwitchTo);
            }

            //Fetch the latest from the server.
            var remote = Enlistment.Network.Remotes["origin"];
            Enlistment.Network.Fetch(remote, new[] { $"{baselineBranchName}:{RefsRemotesPrefix}origin/{baselineBranchName}" }, GetFetchOptions());

            Enlistment.Checkout(branchToSwitchTo, GetCheckoutOptions());

            var remoteBaselineBranchName = baselineBranchName.StartsWith("origin/") ? baselineBranchName : $"origin/{baselineBranchName}";

            var remoteBranch = Enlistment.Branches.FirstOrDefault(b => b.FriendlyName == remoteBaselineBranchName);
            if (remoteBranch == null)
            {
                throw new ArgumentException($"Visual Studio branch {remoteBaselineBranchName} not found.");
            }

            Enlistment.Reset(ResetMode.Hard, remoteBranch.Tip);
            return Enlistment.Head;
        }

        private static async Task<GitPullRequest> CreatePlaceholderBranchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gitClient = ProjectCollection.GetClient<GitHttpClient>();
            var repository = await gitClient.GetRepositoryAsync(project: Options.TFSProjectName, repositoryId: "VS", cancellationToken: cancellationToken);

            var refs = await gitClient.GetRefsAsync(
                repository.Id,
                filter: $"heads/{Options.VisualStudioBranchName}",
                cancellationToken: cancellationToken);
            GitRef sourceBranch = refs.Single(r => r.Name == $"refs/heads/{Options.VisualStudioBranchName}");

            var branchName = GetNewBranchName();

            _ = await gitClient.CreatePushAsync(new GitPush()
            {
                RefUpdates = new[] {
                    new GitRefUpdate()
                    {
                        Name = $"refs/heads/{branchName}",
                        OldObjectId = sourceBranch.ObjectId,
                    }
                },
                Commits = new[] {
                    new GitCommitRef()
                    {
                        Comment = $"PLACEHOLDER INSERTION FOR {Options.InsertionName}",
                        Changes = new GitChange[]
                        {
                            new GitChange()
                            {
                                ChangeType = VersionControlChangeType.Delete,
                                Item = new GitItem() { Path = "/Init.ps1" }
                            },
                        }
                    },
                },
            }, repository.Id, cancellationToken: cancellationToken);

            return await CreatePullRequestAsync(
                branchName,
                $"PLACEHOLDER INSERTION FOR {Options.InsertionName}",
                "Not Specified",
                Options.TitlePrefix,
                cancellationToken);
        }

        private static Branch PushChanges(Branch branch, BuildVersion newRoslynVersion, CancellationToken cancellationToken, bool forcePush = false)
        {
            StageFiles(newRoslynVersion, cancellationToken);
            CommitStagedChanges(newRoslynVersion, cancellationToken);
            return PushChanges(branch, cancellationToken, forcePush: forcePush);
        }

        private static Branch PushChanges(Branch branch, CancellationToken cancellationToken, bool forcePush = false)
        {
            Stopwatch watch;
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Pushing branch");
            watch = Stopwatch.StartNew();
            var destinationSpec = Enlistment.Refs["HEAD"].TargetIdentifier;
            if (forcePush)
            {
                destinationSpec = "+" + destinationSpec;
            }

            Enlistment.Network.Push(Enlistment.Network.Remotes["origin"], destinationSpec, branch.CanonicalName, GetPushOptions());
            Console.WriteLine($"Pushing took {watch.Elapsed.TotalSeconds} seconds");
            return Enlistment.Head;
        }

        private static void StageFiles(BuildVersion newRoslynVersion,CancellationToken cancellationToken)
        {
            var repositoryStatus = Enlistment.RetrieveStatus();
            if (repositoryStatus.IsDirty)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filesToStage = repositoryStatus
                    .Where(item => item.State != FileStatus.Unaltered && item.State != FileStatus.Ignored)
                    .Select(item => item.FilePath).ToList();

                cancellationToken.ThrowIfCancellationRequested();
                if (!isWhitespaceOnlyChange(Enlistment.Diff.Compare<Patch>(filesToStage)))
                {
                    Console.WriteLine($"Staging {filesToStage.Count()} file(s)");
                    var watch = Stopwatch.StartNew();
                    Enlistment.Stage(filesToStage, GetStageOptions());
                    Console.WriteLine($"Staging took {watch.Elapsed.TotalSeconds} seconds");
                }
                else
                {
                    Console.WriteLine("Only whitespace changes found");
                }
            }

            bool isWhitespaceOnlyChange(Patch p)
            {
                var before = new StringBuilder();
                var after = new StringBuilder();
                foreach (var change in p)
                {
                    if (change.Status != ChangeKind.Modified)
                    {
                        return false;
                    }

                    using (var reader = new StringReader(change.Patch))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (!line.StartsWith("---") && !line.StartsWith("+++"))
                            {
                                if (line.StartsWith("+"))
                                {
                                    after.Append(line.Substring(1).Trim());
                                    continue;
                                }
                                else if (line.StartsWith("-"))
                                {
                                    before.Append(line.Substring(1).Trim());
                                    continue;
                                }
                            }

                            after.AppendLine(line);
                            before.AppendLine(line);
                        }
                    }

                    if (after.ToString() != before.ToString())
                    {
                        return false;
                    }

                    after.Clear();
                    before.Clear();
                }
                return true;
            }
        }
    }
}
