using Rug.UI.Core.Tests.E2E;

if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
    throw new PlatformNotSupportedException("Minecraft WGC E2E requires Windows 10 1809 or later.");
return await Phase2EndToEndTests.RunAsync(args);
