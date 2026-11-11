using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Screen transitions carry plain managed records rather than Godot Variants.
/// SceneRouter.GoTo takes object?, so nothing is marshalled through a signal;
/// only EventBus (which is restricted to primitives) uses CSV strings.
/// </summary>
public sealed record DraftPayload(int RunIndex);

public sealed record RunEndPayload(RunEndInfo Info, int RunIndex);
