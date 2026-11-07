param(
    [Parameter(Mandatory)]
    [string]$Path,
    [string]$CandidateRelativePattern,
    [switch]$AsJson
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\', '/')

if (-not ("ADOFAIVfxRenderer.NtfsUsage" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ADOFAIVfxRenderer {
    public static class NtfsUsage {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out FileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetCompressedFileSizeW(string fileName, out uint highSize);

        private static string LongPath(string path) {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        public static string GetFileId(string path, out uint linkCount) {
            using (var file = CreateFileW(LongPath(path), 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                if (file.IsInvalid)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), path);
                FileInformation information;
                if (!GetFileInformationByHandle(file, out information))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), path);
                linkCount = information.NumberOfLinks;
                return information.VolumeSerialNumber.ToString("X8") + ":" +
                       information.FileIndexHigh.ToString("X8") +
                       information.FileIndexLow.ToString("X8");
            }
        }

        public static ulong GetAllocatedBytes(string path) {
            Marshal.SetLastPInvokeError(0);
            uint highSize;
            uint lowSize = GetCompressedFileSizeW(LongPath(path), out highSize);
            int error = Marshal.GetLastWin32Error();
            if (lowSize == 0xFFFFFFFF && error != 0)
                throw new System.ComponentModel.Win32Exception(error, path);
            return ((ulong)highSize << 32) | lowSize;
        }
    }
}
'@
}

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Force -File) {
    $relative = $file.FullName.Substring($root.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
    $top = $relative.Split([IO.Path]::DirectorySeparatorChar)[0]
    $links = [uint32]0
    $id = [ADOFAIVfxRenderer.NtfsUsage]::GetFileId($file.FullName, [ref]$links)
    $rows.Add([pscustomobject]@{
        Top = $top
        Relative = $relative
        Id = $id
        Links = [int]$links
        Logical = [uint64]$file.Length
        Allocated = [uint64]([ADOFAIVfxRenderer.NtfsUsage]::GetAllocatedBytes($file.FullName))
    })
}

$groups = @($rows | Group-Object Id)
$candidateRows = if ([string]::IsNullOrWhiteSpace($CandidateRelativePattern)) {
    @()
} else {
    @($rows | Where-Object Relative -Match $CandidateRelativePattern)
}
$candidateIds = @($candidateRows | Select-Object -ExpandProperty Id -Unique)
$candidateReclaim = if ($candidateIds.Count -eq 0) {
    0
} else {
    ($groups |
        Where-Object {
            $_.Name -in $candidateIds -and
            $_.Count -ge $_.Group[0].Links -and
            @($_.Group | Where-Object Relative -NotMatch $CandidateRelativePattern).Count -eq 0
        } |
        ForEach-Object { $_.Group[0].Allocated } |
        Measure-Object -Sum).Sum
}
$directories = foreach ($item in Get-ChildItem -LiteralPath $root -Force) {
    $directoryRows = @($rows | Where-Object Top -eq $item.Name)
    $logical = ($directoryRows | Measure-Object Logical -Sum).Sum
    $ids = @($directoryRows | Select-Object -ExpandProperty Id -Unique)
    $allocated = ($groups |
        Where-Object Name -in $ids |
        ForEach-Object { $_.Group[0].Allocated } |
        Measure-Object -Sum).Sum
    $exclusive = ($groups |
        Where-Object {
            $tops = @($_.Group.Top | Sort-Object -Unique)
            $tops.Count -eq 1 -and
            $tops[0] -eq $item.Name -and
            $_.Count -ge $_.Group[0].Links
        } |
        ForEach-Object { $_.Group[0].Allocated } |
        Measure-Object -Sum).Sum
    [pscustomobject]@{
        Name = $item.Name
        Files = $directoryRows.Count
        LogicalBytes = [uint64]$logical
        AllocatedBytesSeen = [uint64]$allocated
        ExclusiveReclaimBytes = [uint64]$exclusive
    }
}

$summary = [pscustomobject]@{
    Root = $root
    Files = $rows.Count
    UniqueFileIds = $groups.Count
    LogicalBytes = [uint64](($rows | Measure-Object Logical -Sum).Sum)
    UniqueAllocatedBytes = [uint64](($groups | ForEach-Object { $_.Group[0].Allocated } | Measure-Object -Sum).Sum)
    FullyObservedAllocatedBytes = [uint64](($groups |
        Where-Object { $_.Count -ge $_.Group[0].Links } |
        ForEach-Object { $_.Group[0].Allocated } |
        Measure-Object -Sum).Sum)
    SharedOrExternalFileIds = @($groups | Where-Object {
        @($_.Group.Top | Sort-Object -Unique).Count -gt 1 -or $_.Count -lt $_.Group[0].Links
    }).Count
    CandidateRelativePattern = $CandidateRelativePattern
    CandidateLogicalBytes = [uint64](($candidateRows | Measure-Object Logical -Sum).Sum)
    CandidateReclaimBytes = [uint64]$candidateReclaim
    Directories = @($directories | Sort-Object ExclusiveReclaimBytes -Descending)
}

if ($AsJson) {
    $summary | ConvertTo-Json -Depth 5
} else {
    $summary.Directories | Select-Object Name, Files,
        @{Name="LogicalGB"; Expression={ [math]::Round($_.LogicalBytes / 1GB, 3) }},
        @{Name="AllocatedSeenGB"; Expression={ [math]::Round($_.AllocatedBytesSeen / 1GB, 3) }},
        @{Name="ExclusiveReclaimGB"; Expression={ [math]::Round($_.ExclusiveReclaimBytes / 1GB, 3) }} |
        Format-Table -AutoSize
    $summary | Select-Object Root, Files, UniqueFileIds,
        @{Name="LogicalGB"; Expression={ [math]::Round($_.LogicalBytes / 1GB, 3) }},
        @{Name="UniqueAllocatedGB"; Expression={ [math]::Round($_.UniqueAllocatedBytes / 1GB, 3) }},
        @{Name="FullyObservedAllocatedGB"; Expression={ [math]::Round($_.FullyObservedAllocatedBytes / 1GB, 3) }},
        SharedOrExternalFileIds, CandidateRelativePattern,
        @{Name="CandidateLogicalGB"; Expression={ [math]::Round($_.CandidateLogicalBytes / 1GB, 3) }},
        @{Name="CandidateReclaimGB"; Expression={ [math]::Round($_.CandidateReclaimBytes / 1GB, 3) }} |
        Format-List
}
