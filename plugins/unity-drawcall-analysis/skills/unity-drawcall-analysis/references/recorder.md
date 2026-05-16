# Variant Recorder — Combining FrameDebugger + ShaderPass Metadata

This shows how to merge FrameDebugger capture data with ShaderUtil pass metadata
to build a pass-aware shader variant collection.

## Key Pattern: Resolving Pass Identity

The core problem this solves: Unity's built-in `ShaderVariantCollection` only tracks
`PassType`, but different passes can share the same `PassType`. The FrameDebugger gives
us the exact `passIndex`, which we can resolve to a specific pass via `ShaderPassExtractor.FindByIndex`.

```csharp
// Given a captured draw-call event with passIndex:
var passInfo = ShaderPassExtractor.FindByIndex(shader, drawCall.passIndex);
if (passInfo != null)
{
    // Now we know: subShaderIndex, passName, lightMode, exact PassType
    string passName = passInfo.passName;
    string lightMode = passInfo.lightMode;
    PassType passType = (PassType)passInfo.passType;
}
```

If `passIndex` is -1 (FrameDebugger couldn't determine it):
```csharp
var passInfo = ShaderPassExtractor.FindBestMatch(
    shader, PassType.Normal, drawCall.passName, drawCall.lightMode);
```

## Keyword Normalization

FrameDebugger returns keywords as a space-separated string. Normalize for stable comparison:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

public static class KeywordUtils
{
    public static List<string> Normalize(IEnumerable<string> keywords)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (keywords != null)
            foreach (var kw in keywords)
                if (!string.IsNullOrWhiteSpace(kw))
                    set.Add(kw.Trim());

        var result = new List<string>(set);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public static List<string> Normalize(string rawKeywords)
    {
        if (string.IsNullOrWhiteSpace(rawKeywords))
            return new List<string>();
        return Normalize(rawKeywords.Split(
            new[] { ' ', '\t', '\r', '\n', ';', ',' },
            StringSplitOptions.RemoveEmptyEntries));
    }

    public static string ToKey(IEnumerable<string> keywords)
    {
        return string.Join(" ", Normalize(keywords));
    }
}
```

## Merging FrameDebugger Events with Unity SVC

When you have both a Unity `ShaderVariantCollection` (from rendering) and FrameDebugger data,
merge them: for each variant from the SVC, try to find a matching draw-call event and
use its passIndex for more precise pass identity.

```csharp
// For each variant from Unity SVC:
foreach (var variant in unityVariants)
{
    var keywords = KeywordUtils.Normalize(variant.keywords);
    var matchedDrawCalls = FindDrawCalls(shader, variant.passType, keywords);

    if (matchedDrawCalls.Count == 0)
    {
        // No FD data — fall back to PassType-only record
        AddRecord(records, shader, variant.passType, keywords, null, null);
        continue;
    }

    foreach (var drawCall in matchedDrawCalls)
    {
        // Resolve pass identity from draw-call passIndex
        var pass = drawCall.passIndex >= 0
            ? ShaderPassExtractor.FindByIndex(shader, drawCall.passIndex)
            : ShaderPassExtractor.FindBestMatch(
                shader, variant.passType, drawCall.passName, drawCall.lightMode);
        AddRecord(records, shader, variant.passType, keywords, pass, drawCall);
    }
}
```

## Deduplication Key

To deduplicate variant records, use a composite key:
```
$"{shader.name}|{passIndex}|{keywordKey}|{passName}|{lightMode}"
```

This ensures each unique (shader, pass, keyword combo) appears only once,
even when the same pass is rendered in multiple frames or scenes.

## Complete Record Data Model

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class ShaderVariantRecord
{
    // Pass identity (enriched from ShaderPassExtractor)
    public int passType = (int)PassType.Normal;
    public string passTypeName = PassType.Normal.ToString();
    public int passIndex = -1;
    public int subShaderIndex = -1;
    public int localPassIndex = -1;
    public string passName;
    public string lightMode;

    // Variant data
    public List<string> keywords = new();

    // Observational metadata (from FrameDebugger)
    public List<string> scenePaths = new();
    public List<string> frameEventNames = new();
}
```

## Output Format

Save one file per shader beside the Unity `.shadervariants`:

```
Assets/MyShader.shader
  → Assets/ShaderVariants/Assets/MyShader.shadervariants  (Unity SVC)
  → Assets/ShaderVariants/Assets/MyShader.utsvc            (pass-aware UTSVC)
```

Use ScriptableObject for the output file so Unity can reference it in the asset pipeline,
or JSON if you need diffability and version control friendliness.
