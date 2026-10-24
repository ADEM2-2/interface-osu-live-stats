using System.Diagnostics;

namespace OsuCompanion.ProcessManagement;

public static class OsuProcessLocator
{
    public static Process? Find()
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("osu!");
        }
        catch
        {
            return null;
        }

        foreach (var process in candidates)
        {
            try
            {
                if (!process.HasExited)
                {
                    foreach (var other in candidates)
                    {
                        if (!ReferenceEquals(other, process))
                        {
                            other.Dispose();
                        }
                    }
                    return process;
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return null;
    }
}
