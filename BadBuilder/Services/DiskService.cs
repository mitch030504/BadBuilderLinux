using System;
using System.Collections.Generic;
using System.Text;

namespace BadBuilder.Services;

internal static class DiskService
{
    
}

internal sealed record DiskInfo(
    string ID,
    string Name,
    long Size,
    DriveType Type,
    string DevicePath);