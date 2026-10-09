namespace C64Basic.Tests;

/// <summary>
/// A test that needs the ROM dumps in <c>roms/</c> (copyrighted, git-ignored). Without them it shows as skipped, not as passed, so a
/// run on a machine without the dumps (CI) says plainly how much it did not check.
/// </summary>
public sealed class RomFactAttribute : FactAttribute
{
    public RomFactAttribute()
    {
        if (TestRoms.Find() == null) Skip = "needs the C64 and 1541 ROM dumps in roms/";
    }
}

/// <summary>A test that needs a local game image in <c>roms/games</c> (and the ROM dumps).</summary>
public sealed class GameFactAttribute : FactAttribute
{
    public GameFactAttribute(string pattern)
    {
        if (TestRoms.Find() == null) Skip = "needs the C64 and 1541 ROM dumps in roms/";
        else if (TestRoms.FindGame(pattern) == null) Skip = "needs a game image matching " + pattern + " in roms/games";
    }
}
