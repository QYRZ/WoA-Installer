﻿using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace Installer.Core
{
    public class WindowsDeployer : IWindowsDeployer
    {
        private const ulong SpaceNeeded = 18 * (ulong) 1_000_000_000;
        private readonly IConfigProvider configProvider;
        private readonly DriverLocations driverLocations = new DriverLocations();
        private readonly ILowLevelApi lowLevelApi;
        private readonly IWindowsImageService windowsImageService;
        private BcdInvoker2 bcd;

        public WindowsDeployer(ILowLevelApi lowLevelApi, IConfigProvider configProvider, IWindowsImageService windowsImageService)
        {
            this.lowLevelApi = lowLevelApi;
            this.configProvider = configProvider;
            this.windowsImageService = windowsImageService;
        }

        public async Task Deploy(string imagePath, int imageIndex = 1, IObserver<double> progressObserver = null)
        {
            Log.Information("Deploying Windows ARM64...");

            await RemoveExistingWindowsPartitions();
            await AllocateSpace();
            var partitions = await CreatePartitions();
            
            await DeployWindows(partitions, imagePath, imageIndex, progressObserver);
            await InjectBasicDrivers(partitions.Windows);
            await MakeBootable(partitions);
        }

        private Task MakeBootable(WindowsVolumes volumes)
        {
            Log.Information("Making Windows installation bootable...");

            bcd = new BcdInvoker2(Path.Combine(volumes.Boot.RootDir.Name, "EFI", "Microsoft", "Boot"));
            CmdUtils.Run(@"c:\Windows\SysNative\bcdboot.exe", $@"{Path.Combine(volumes.Windows.RootDir.Name, "Windows")} /f UEFI /s {volumes.Boot.Letter}:");
            bcd.Invoke("/set {default} testsigning on");
            bcd.Invoke("/set {default} nointegritychecks on");

            return Task.CompletedTask;
        }

        private Task RemoveExistingWindowsPartitions()
        {
            Log.Information("Removing existing partitions...");

            return lowLevelApi.RemoveExistingWindowsPartitions();
        }

        private Task InjectBasicDrivers(Volume partitionsWindows)
        {
            Log.Information("Injecting Basic Drivers...");
            return windowsImageService.InjectDrivers(driverLocations.PreObee, partitionsWindows);
        }

        private async Task DeployWindows(WindowsVolumes volumes, string imagePath, int imageIndex = 1, IObserver<double> progressObserver = null)
        {
            Log.Information("Applying Windows Image...");
            await windowsImageService.ApplyImage(volumes.Windows, imagePath, imageIndex, progressObserver);
        }

        private async Task<WindowsVolumes> CreatePartitions()
        {
            Log.Information("Creating Windows partitions...");

            var disk = (await configProvider.Retrieve()).PhoneDisk;

            await lowLevelApi.CreateReservedPartition(disk, 200 * 1_000_000);

            var bootPartition = await lowLevelApi.CreatePartition(disk, 100 * 1_000_000);
            var bootVolume = await lowLevelApi.GetVolume(bootPartition);
            await lowLevelApi.Format(bootVolume, FileSystemFormat.Fat32, "BOOT");
            await lowLevelApi.AssignDriveLetter(bootVolume, await lowLevelApi.GetFreeDriveLetter());
            
            var windowsPartition = await lowLevelApi.CreatePartition(disk, ulong.MaxValue);
            var winVolume = await lowLevelApi.GetVolume(windowsPartition);
            await lowLevelApi.Format(winVolume, FileSystemFormat.Ntfs, "WindowsARM");
            await lowLevelApi.AssignDriveLetter(winVolume, await lowLevelApi.GetFreeDriveLetter());
            
            return new WindowsVolumes(await lowLevelApi.GetVolume(bootPartition), await lowLevelApi.GetVolume(windowsPartition));
        }

        private async Task AllocateSpace()
        {
            Log.Information("Verifying the available space");

            var disk = (await configProvider.Retrieve()).PhoneDisk;
            var available = disk.Size - disk.AllocatedSize;

            if (available < SpaceNeeded)
            {
                Log.Warning("Currently, there's not enough space in the phone");
                await TakeSpaceFromDataPartition();
            }
        }

        private async Task TakeSpaceFromDataPartition()
        {
            var dataVolume = (await configProvider.Retrieve()).DataVolume;

            Log.Warning("Trying to resize Data partition...");
            var finalSize = dataVolume.Size - SpaceNeeded;
            await lowLevelApi.ResizePartition(dataVolume.Partition, finalSize);
        }

        private class WindowsVolumes
        {
            public WindowsVolumes(Volume bootPartition, Volume windowsPartition)
            {
                Boot = bootPartition;
                Windows = windowsPartition;
            }

            public Volume Boot { get; }
            public Volume Windows { get; }
        }

        private class DriverLocations
        {
            public string PreObee { get; } = Path.Combine(@"Files\Drivers\Stable");
            public string PostObee { get; } = Path.Combine(@"Files\Drivers\Testing");
        }
    }
}