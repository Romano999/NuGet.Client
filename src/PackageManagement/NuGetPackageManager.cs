﻿using NuGet.Client;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.PackagingCore;
using NuGet.ProjectManagement;
using NuGet.Resolver;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Cache;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.PackageManagement
{
    /// <summary>
    /// NuGetPackageManager orchestrates a nuget package operation such as an install or uninstall
    /// It is to be called by various NuGet Clients including the custom third-party ones
    /// </summary>
    public class NuGetPackageManager
    {
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageInstalling;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageInstalled;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageUninstalling;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageUninstalled;

        private ISourceRepositoryProvider SourceRepositoryProvider { get; set; }

        private ISolutionManager SolutionManager { get; set; }

        private ISettings Settings { get; set; }

        public string PackagesFolderPath { get; set; }

        public PackagePathResolver PackagePathResolver { get; set; }

        public SourceRepository PackagesFolderSourceRepository { get; set; }

        private HttpClient HttpClient { get; set; }
        
        /// <summary>
        /// To construct a NuGetPackageManager that does not need a SolutionManager like NuGet.exe
        /// </summary>
        public NuGetPackageManager(ISourceRepositoryProvider sourceRepositoryProvider, string packagesFolderPath, HttpClient httpClient = null)
        {
            InitializeMandatory(sourceRepositoryProvider, httpClient);
            PackagesFolderPath = packagesFolderPath;
            InitializePackagesFolderInfo();
        }

        /// <summary>
        /// To construct a NuGetPackageManager with a mandatory SolutionManager lke VS
        /// </summary>
        public NuGetPackageManager(ISourceRepositoryProvider sourceRepositoryProvider, ISettings settings, ISolutionManager solutionManager, HttpClient httpClient = null/*, IPackageResolver packageResolver */)
        {
            InitializeMandatory(sourceRepositoryProvider, httpClient);
            if(solutionManager == null)
            {
                throw new ArgumentNullException("solutionManager");
            }
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            Settings = settings;
            SolutionManager = solutionManager;

            PackagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(SolutionManager, Settings);
            InitializePackagesFolderInfo();
        }

        private void InitializeMandatory(ISourceRepositoryProvider sourceRepositoryProvider, HttpClient httpClient)
        {
            if (sourceRepositoryProvider == null)
            {
                throw new ArgumentNullException("sourceRepositoryProvider");
            }

            SourceRepositoryProvider = sourceRepositoryProvider;
            if (httpClient == null)
            {
                httpClient = new HttpClient(new WebRequestHandler()
                {
                    CachePolicy = new RequestCachePolicy(RequestCacheLevel.Default)
                });
            }
            HttpClient = httpClient;
        }

        private void InitializePackagesFolderInfo()
        {            
            PackagePathResolver = new PackagePathResolver(PackagesFolderPath);
            PackagesFolderSourceRepository = SourceRepositoryProvider.CreateRepository(new PackageSource(PackagesFolderPath));
        }

        /// <summary>
        /// Installs the latest version of the given <param name="packageId"></param> to NuGetProject <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task InstallPackageAsync(NuGetProject nuGetProject, string packageId, ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext)
        {
            // Step-1 : Get latest version for packageId
            var latestVersion = await GetLatestVersionAsync(packageId, resolutionContext);

            if(latestVersion == null)
            {
                throw new InvalidOperationException(String.Format(Strings.NoLatestVersionFound, packageId));
            }

            // Step-2 : Call InstallPackageAsync(project, packageIdentity)
            await InstallPackageAsync(nuGetProject, new PackageIdentity(packageId, latestVersion), resolutionContext, nuGetProjectContext);
        }

        /// <summary>
        /// Installs given <param name="packageIdentity"></param> to NuGetProject <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task InstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity, ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext)
        {
            // Step-1 : Call PreviewInstallPackagesAsync to get all the nuGetProjectActions
            var nuGetProjectActions = await PreviewInstallPackageAsync(nuGetProject, packageIdentity, resolutionContext, nuGetProjectContext);

            // Step-2 : Execute all the nuGetProjectActions
            await ExecuteNuGetProjectActionsAsync(nuGetProject, nuGetProjectActions, nuGetProjectContext);
        }

        public async Task UninstallPackageAsync(NuGetProject nuGetProject, string packageId, UninstallationContext uninstallationContext, INuGetProjectContext nuGetProjectContext)
        {
            // Step-1 : Call PreviewUninstallPackagesAsync to get all the nuGetProjectActions
            var nuGetProjectActions = await PreviewUninstallPackageAsync(nuGetProject, packageId, uninstallationContext, nuGetProjectContext);

            // Step-2 : Execute all the nuGetProjectActions
            await ExecuteNuGetProjectActionsAsync(nuGetProject, nuGetProjectActions, nuGetProjectContext);
        }

        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to install <param name="packageId"></param> into <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewInstallPackageAsync(NuGetProject nuGetProject, string packageId, ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext)
        {
            // Step-1 : Get latest version for packageId
            var latestVersion = await GetLatestVersionAsync(packageId, resolutionContext);

            if (latestVersion == null)
            {
                throw new InvalidOperationException(Strings.NoLatestVersionFound);
            }

            // Step-2 : Call InstallPackage(project, packageIdentity)
            return await PreviewInstallPackageAsync(nuGetProject, new PackageIdentity(packageId, latestVersion), resolutionContext, nuGetProjectContext);
        }

        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to install <param name="packageIdentity"></param> into <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewInstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity, ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext)
        {
            List<NuGetProjectAction> nuGetProjectActions = new List<NuGetProjectAction>();
            // TODO: these sources should be ordered
            // TODO: search in only the active source but allow dependencies to come from other sources?

            try
            {
                var enabledSources = SourceRepositoryProvider.GetRepositories().Where(e => e.PackageSource.IsEnabled);
                if (resolutionContext.DependencyBehavior != DependencyBehavior.Ignore)
                {                    
                    var packagesToInstall = new List<PackageIdentity>() { packageIdentity };
                    // Step-1 : Get metadata resources using gatherer
                    var targetFramework = nuGetProject.GetMetadata<NuGetFramework>(NuGetProjectMetadataKeys.TargetFramework);
                    nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToGatherDependencyInfo, packageIdentity, targetFramework);
                    var availablePackageDependencyInfoWithSourceSet = await ResolverGather.GatherPackageDependencyInfo(resolutionContext, packageIdentity, targetFramework, enabledSources, CancellationToken.None);

                    if (!availablePackageDependencyInfoWithSourceSet.Any())
                    {
                        throw new InvalidOperationException(String.Format(Strings.UnableToGatherDependencyInfo, packageIdentity));
                    }

                    // Step-2 : Call IPackageResolver.Resolve to get new list of installed packages
                    var projectInstalledPackageReferences = nuGetProject.GetInstalledPackages();
                    // TODO: Consider using IPackageResolver once it is extensible
                    var packageResolver = new PackageResolver(resolutionContext.DependencyBehavior);
                    nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToResolveDependencies, packageIdentity, resolutionContext.DependencyBehavior);
                    IEnumerable<PackageIdentity> newListOfInstalledPackages = packageResolver.Resolve(packagesToInstall, availablePackageDependencyInfoWithSourceSet, projectInstalledPackageReferences);
                    if (newListOfInstalledPackages == null)
                    {
                        throw new InvalidOperationException(String.Format(Strings.UnableToResolveDependencyInfo, packageIdentity, resolutionContext.DependencyBehavior));
                    }

                    // Step-3 : Get the list of nuGetProjectActions to perform, install/uninstall on the nugetproject
                    // based on newPackages obtained in Step-2 and project.GetInstalledPackages
                    var oldListOfInstalledPackages = projectInstalledPackageReferences.Select(p => p.PackageIdentity);

                    nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvingActionsToInstallPackage, packageIdentity);
                    var newPackagesToUninstall = oldListOfInstalledPackages
                        .Where(op => newListOfInstalledPackages
                            .Where(np => op.Id.Equals(np.Id, StringComparison.OrdinalIgnoreCase) && !op.Version.Equals(np.Version)).Any());
                    var newPackagesToInstall = newListOfInstalledPackages.Where(p => !oldListOfInstalledPackages.Contains(p));

                    foreach (PackageIdentity newPackageToUninstall in newPackagesToUninstall)
                    {
                        nuGetProjectActions.Add(NuGetProjectAction.CreateUninstallProjectAction(newPackageToUninstall));
                    }

                    var comparer = PackageIdentity.Comparer;

                    foreach (PackageIdentity newPackageToInstall in newPackagesToInstall)
                    {
                        // find the package match based on identity
                        SourceDependencyInfo sourceDepInfo = availablePackageDependencyInfoWithSourceSet.Where(p => comparer.Equals(p, newPackageToInstall)).SingleOrDefault();

                        if (sourceDepInfo == null)
                        {
                            // this really should never happen
                            throw new InvalidOperationException(String.Format(Strings.PackageNotFound, packageIdentity));
                        }

                        nuGetProjectActions.Add(NuGetProjectAction.CreateInstallProjectAction(newPackageToInstall, sourceDepInfo.Source));
                    }
                }
                else
                {
                    var sourceRepository = await GetSourceRepository(packageIdentity, enabledSources);
                    nuGetProjectActions.Add(NuGetProjectAction.CreateInstallProjectAction(packageIdentity, sourceRepository));
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(String.Format(Strings.PackageCouldNotBeInstalled, packageIdentity), ex);
            }

            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvedActionsToInstallPackage, packageIdentity);
            return nuGetProjectActions;
        }

        private static async Task<SourceRepository> GetSourceRepository(PackageIdentity packageIdentity, IEnumerable<SourceRepository> sourceRepositories)
        {
            Uri downloadUrl;
            foreach(var sourceRepository in sourceRepositories)
            {
                try
                {
                    var downloadResource = sourceRepository.GetResource<DownloadResource>();
                    downloadUrl = await downloadResource.GetDownloadUrl(packageIdentity);
                    return sourceRepository;
                }
                catch (Exception)
                {
                    downloadUrl = null;
                }
            }

            throw new InvalidOperationException(String.Format(Strings.PackageCouldNotBeInstalled, packageIdentity));
        }

        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to uninstall <param name="packageId"></param> into <param name="nuGetProject"></param>
        /// <param name="uninstallationContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewUninstallPackageAsync(NuGetProject nuGetProject, string packageId, UninstallationContext uninstallationContext, INuGetProjectContext nuGetProjectContext)
        {
            if(SolutionManager == null)
            {
                throw new InvalidOperationException(Strings.SolutionManagerNotAvailableForUninstall);
            }

            // Step-0: Get the packageIdentity corresponding to packageId and check if it exists to be uninstalled
            var installedPackages = nuGetProject.GetInstalledPackages();
            PackageReference packageReference = installedPackages
                .Where(pr => pr.PackageIdentity.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (packageReference == null || packageReference.PackageIdentity == null)
            {
                throw new ArgumentException(String.Format(Strings.PackageToBeUninstalledCouldNotBeFound,
                    packageId, nuGetProject.GetMetadata<string>(NuGetProjectMetadataKeys.Name)));
            }

            // Step-1 : Get the metadata resources from "packages" folder or custom repository path
            var packageIdentity = packageReference.PackageIdentity;
            var packageReferenceTargetFramework = packageReference.TargetFramework;
            nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToGatherDependencyInfo, packageIdentity, packageReferenceTargetFramework);

            // TODO: IncludePrerelease is a big question mark
            var installedPackageIdentities = installedPackages.Select(pr => pr.PackageIdentity);
            var dependencyInfoFromPackagesFolder = await GetDependencyInfoFromPackagesFolder(installedPackageIdentities,
                packageReferenceTargetFramework, includePrerelease: false);

            // Step-2 : Determine if the package can be uninstalled based on the metadata resources
            UninstallResolver.GetPackagesToBeUninstalled(packageIdentity, dependencyInfoFromPackagesFolder, installedPackageIdentities, uninstallationContext);

            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvingActionsToUninstallPackage, packageIdentity);
            // TODO:

            // TODO: Stuff here
            var nuGetProjectActions = new NuGetProjectAction[] { NuGetProjectAction.CreateUninstallProjectAction(packageIdentity) };

            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvedActionsToUninstallPackage, packageIdentity);
            return nuGetProjectActions;
        }

        private async Task<IEnumerable<PackageDependencyInfo>> GetDependencyInfoFromPackagesFolder(IEnumerable<PackageIdentity> packageIdentities,
            NuGetFramework nuGetFramework,
            bool includePrerelease)
        {
            try
            {
                var dependencyInfoResource = await PackagesFolderSourceRepository.GetResourceAsync<DepedencyInfoResource>();
                return await dependencyInfoResource.ResolvePackages(packageIdentities, nuGetFramework, includePrerelease);
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Executes the list of <param name="nuGetProjectActions"></param> on <param name="nuGetProject"></param>, which is likely obtained by calling into PreviewInstallPackageAsync
        /// <param name="nuGetProjectContext"></param> is used in the process
        /// </summary>
        public async Task ExecuteNuGetProjectActionsAsync(NuGetProject nuGetProject, IEnumerable<NuGetProjectAction> nuGetProjectActions, INuGetProjectContext nuGetProjectContext)
        {
            foreach (NuGetProjectAction nuGetProjectAction in nuGetProjectActions)
            {
                if (nuGetProjectAction.NuGetProjectActionType == NuGetProjectActionType.Uninstall)
                {
                    ExecuteUninstall(nuGetProject, nuGetProjectAction.PackageIdentity, nuGetProjectContext);
                }
                else
                {
                    using (var targetPackageStream = new MemoryStream())
                    {
                        await PackageDownloader.GetPackageStream(HttpClient, nuGetProjectAction.SourceRepository, nuGetProjectAction.PackageIdentity, targetPackageStream);
                        ExecuteInstall(nuGetProject, nuGetProjectAction.PackageIdentity, targetPackageStream, nuGetProjectContext);
                    }
                }
            }
        }

        private void ExecuteInstall(NuGetProject nuGetProject, PackageIdentity packageIdentity, Stream packageStream, INuGetProjectContext nuGetProjectContext)
        {
            var packageOperationEventArgs = new PackageOperationEventArgs(packageIdentity);
            if(PackageInstalling != null)
            {
                PackageInstalling(this, packageOperationEventArgs);
            }
            nuGetProject.InstallPackage(packageIdentity, packageStream, nuGetProjectContext);

            // TODO: Consider using CancelEventArgs instead of a regular EventArgs??
            //if (packageOperationEventArgs.Cancel)
            //{
            //    return;
            //}

            if(PackageInstalled != null)
            {
                PackageInstalled(this, packageOperationEventArgs);
            }
        }

        private void ExecuteUninstall(NuGetProject nuGetProject, PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext)
        {
            // Step-1: Raise package uninstalling event
            var packageOperationEventArgs = new PackageOperationEventArgs(packageIdentity);
            if (PackageUninstalling != null)
            {
                PackageUninstalling(this, packageOperationEventArgs);
            }

            // Step-2: Call nuGetProject.UninstallPackage
            nuGetProject.UninstallPackage(packageIdentity, nuGetProjectContext);

            // Step-3: Check if the package directory could be deleted
            if(!PackageExistsInAnotherNuGetProject(nuGetProject, packageIdentity, nuGetProjectContext))
            {
                DeletePackageDirectory(packageIdentity, nuGetProjectContext);
            }

            // TODO: Consider using CancelEventArgs instead of a regular EventArgs??
            //if (packageOperationEventArgs.Cancel)
            //{
            //    return;
            //}

            // Step-4: Raise PackageUninstalled event
            if (PackageUninstalled != null)
            {
                PackageUninstalled(this, packageOperationEventArgs);
            }
        }

        private bool PackageExistsInAnotherNuGetProject(NuGetProject nuGetProject, PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext)
        {
            bool packageExistsInAnotherNuGetProject = false;
            var nuGetProjectName = nuGetProject.GetMetadata<string>(NuGetProjectMetadataKeys.Name);
            foreach (var otherNuGetProject in SolutionManager.GetNuGetProjects())
            {
                if (!otherNuGetProject.GetMetadata<string>(NuGetProjectMetadataKeys.Name).Equals(nuGetProjectName, StringComparison.OrdinalIgnoreCase))
                {
                    packageExistsInAnotherNuGetProject = otherNuGetProject.GetInstalledPackages().Where(pr => pr.PackageIdentity.Equals(packageIdentity)).Any();
                }
            }

            return packageExistsInAnotherNuGetProject;
        }

        private bool DeletePackageDirectory(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext)
        {
            if(packageIdentity == null)
            {
                throw new ArgumentNullException("packageIdentity");
            }

            if(nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            // TODO: Handle removing of satellite files from the runtime package also

            // 1. Check if the Package exists at root, if not, return false
            if (!PackageExistsInPackagesFolder(packageIdentity))
            {
                nuGetProjectContext.Log(MessageLevel.Warning, NuGet.ProjectManagement.Strings.PackageDoesNotExistInFolder, packageIdentity, PackagesFolderPath);
                return false;
            }

            nuGetProjectContext.Log(MessageLevel.Info, NuGet.ProjectManagement.Strings.RemovingPackageFromFolder, packageIdentity, PackagesFolderPath);
            // 2. Delete the package folder and files from the root directory of this FileSystemNuGetProject
            // Remember that the following code may throw System.UnauthorizedAccessException
            Directory.Delete(PackagePathResolver.GetInstallPath(packageIdentity), recursive: true);
            nuGetProjectContext.Log(MessageLevel.Info, NuGet.ProjectManagement.Strings.RemovedPackageFromFolder, packageIdentity, PackagesFolderPath);
            return true;
        }

        /// <summary>
        /// A package is considered to exist in FileSystemNuGetProject, if the 'nupkg' file is present where expected
        /// </summary>
        private bool PackageExistsInPackagesFolder(PackageIdentity packageIdentity)
        {
            string packageFileFullPath = Path.Combine(PackagePathResolver.GetInstallPath(packageIdentity),
                PackagePathResolver.GetPackageFileName(packageIdentity));
            return File.Exists(packageFileFullPath);
        }

        private async Task<NuGetVersion> GetLatestVersionAsync(string packageId, ResolutionContext resolutionContext)
        {
            List<NuGetVersion> latestVersionFromDifferentRepositories = new List<NuGetVersion>();
            foreach (var sourceRepository in SourceRepositoryProvider.GetRepositories())
            {
                var metadataResource = sourceRepository.GetResource<MetadataResource>();
                if (metadataResource != null)
                {
                    var latestVersionKeyPairList = await metadataResource.GetLatestVersions(new List<string>() { packageId },
                        resolutionContext.IncludePrerelease, resolutionContext.IncludeUnlisted, CancellationToken.None);
                    if((latestVersionKeyPairList == null || !latestVersionKeyPairList.Any()))
                    {
                        continue;
                    }
                    latestVersionFromDifferentRepositories.Add(latestVersionKeyPairList.FirstOrDefault().Value);
                }
            }

            return latestVersionFromDifferentRepositories.Count == 0 ? null : latestVersionFromDifferentRepositories.Max<NuGetVersion>();
        }
    }

    /// <summary>
    /// The event args class used in raising package operation events
    /// </summary>
    public  class PackageOperationEventArgs : EventArgs
    {
        PackageIdentity PackageIdentity { get; set; }
        /// <summary>
        /// Creates a package operation event args object for given <param name="packageIdentity"></param>
        /// </summary>
        public PackageOperationEventArgs(PackageIdentity packageIdentity)
        {
            PackageIdentity = packageIdentity;
        }
    }
}
