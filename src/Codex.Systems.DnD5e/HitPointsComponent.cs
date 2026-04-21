using System;
using Codex.Core.Components;

namespace Codex.Systems.DnD5e;

[Obsolete("Use ResourcePoolComponent with 'HP' and 'HP_Max' keys instead.")]
public struct HitPointsComponent
{
    public int Current { get; set; }
    public int Maximum { get; set; }
}