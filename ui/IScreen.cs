using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>Screens are independently instancable scenes; the router only knows this contract.</summary>
public interface IScreen
{
    void OnEnter(object? payload);
    void OnExit();

    /// <summary>Android hardware back. Must NEVER exit the app abruptly (GDD 11.5).</summary>
    void OnBack();
}

public enum ScreenId
{
    Home,
    Forge,
    RuneDraft,
    RunEnd
}

/// <summary>
/// The forge scene is pooled, not reloaded. ResetFor reuses tile nodes and
/// buffers between forges — zero allocations per transition (GDD 11.5).
/// </summary>
public interface IForgeScreen : IScreen
{
    void ResetFor(ForgeData forge, int runIndex, RuneEffects effects);
}
