using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace ManualSigmas;

/// <summary>Adds a 'Manual' scheduler, which uses a user-provided list of sigmas.</summary>
public class ManualSigmasExtension : Extension
{
    /// <summary>The user's list of sigmas, used by any sampler whose scheduler is 'manual'.</summary>
    public static T2IRegisteredParam<string> ManualSigmas;

    /// <summary>The user's list of sigmas for the refiner stage, falling back to <see cref="ManualSigmas"/> if empty.</summary>
    public static T2IRegisteredParam<string> RefinerManualSigmas;

    /// <summary>Node types that take 'model', 'scheduler' and 'steps' inputs, checked for a sigma count mismatch.</summary>
    public static string[] ScheduledNodeTypes = ["SwarmKSampler", "KSamplerAdvanced", "KSampler", "SwarmUnsampler", "BasicScheduler"];

    public override void OnPreInit()
    {
        ScriptFiles.Add("Assets/manual_sigmas.js");
        ComfyUISelfStartBackend.CustomNodePaths.Add(Path.GetFullPath($"{FilePath}ComfyNodes"));
        ComfyUIBackendExtension.NodeToFeatureMap["SetManualSigmas"] = "manual_sigmas";
    }

    public override void OnInit()
    {
        lock (ComfyUIBackendExtension.ValueAssignmentLocker)
        {
            T2IParamTypes.ConcatDropdownValsClean(ref ComfyUIBackendExtension.Schedulers, ["manual///Manual (Custom Sigmas)"]);
        }
        ManualSigmas = T2IParamTypes.Register<string>(new("Manual Sigmas", "List of sigmas to use when the Scheduler is set to 'Manual'.\nSeparate values with commas or spaces, and use '.' for decimals.\nThere must be exactly one more sigma than Steps (eg 21 sigmas for 20 steps).\nThe list should normally end with 0.\nAlso used by the refiner if Refiner Manual Sigmas is empty.\nCan be set per section, eg '<video><param[scheduler]:manual><param[manualsigmas]:1, 0.5, 0>'.",
            "", IgnoreIf: "", FeatureFlag: "manual_sigmas", Group: T2IParamTypes.GroupSampling, OrderPriority: -3.9, CanSectionalize: true, Examples: ["1, 0.75, 0.5, 0.25, 0", "14.6, 6.5, 3.2, 1.5, 0.6, 0.03, 0"]
            ));
        RefinerManualSigmas = T2IParamTypes.Register<string>(new("Refiner Manual Sigmas", "List of sigmas for the Refine/Upscale stage, used when the Refiner Scheduler is set to 'Manual'.\nSame format as Manual Sigmas, with exactly one more sigma than Refiner Steps (or Steps if unset).\nAs with other schedulers, the refiner only runs the end of the list, based on Refiner Control Percentage.\nIf empty, Manual Sigmas is used.",
            "", IgnoreIf: "", FeatureFlag: "manual_sigmas", Group: T2IParamTypes.GroupRefinerOverrides, OrderPriority: -1.4, Examples: ["1, 0.75, 0.5, 0.25, 0", "14.6, 6.5, 3.2, 1.5, 0.6, 0.03, 0"]
            ));
        WorkflowGenerator.AddModelGenStep(g =>
        {
            int section = LoadingSection(g);
            if (SchedulerFor(g.UserInput, section) == "manual")
            {
                string node = g.CreateNode("SetManualSigmas", new JObject()
                {
                    ["model"] = g.LoadingModel,
                    ["sigmas"] = string.Join(", ", GetSigmas(g.UserInput, section).Select(s => s.ToString(CultureInfo.InvariantCulture)))
                });
                g.LoadingModel = [node, 0];
            }
        }, 50);
        WorkflowGenerator.AddStep(g =>
        {
            foreach (string classType in ScheduledNodeTypes)
            {
                g.RunOnNodesOfClass(classType, (_, data) =>
                {
                    if ($"{data["inputs"]["scheduler"]}" != "manual" || data["inputs"]["steps"] is not JValue steps)
                    {
                        return;
                    }
                    string sigmas = FindUpstreamSigmas(g, data["inputs"]["model"] as JArray);
                    if (sigmas is null)
                    {
                        return;
                    }
                    int stepCount = ParseSigmas(sigmas).Count - 1;
                    if (steps.Value<int>() != stepCount)
                    {
                        throw new SwarmUserErrorException($"Manual Sigmas has {stepCount + 1} values ({stepCount} steps), but a sampler using it is set to {steps} steps. Set Steps to {stepCount} for that section, or change the sigma list.");
                    }
                });
            }
        }, 250);
    }

    /// <summary>Returns the prompt section of the model currently being loaded, matching the core LoRA confinement logic.</summary>
    public static int LoadingSection(WorkflowGenerator g)
    {
        if (g.IsRefinerStage)
        {
            return T2IParamInput.SectionID_Refiner;
        }
        else if (g.IsPixelDecoderStage)
        {
            return T2IParamInput.SectionID_PixelDecoder;
        }
        else if (g.IsImageToVideoSwap)
        {
            return T2IParamInput.SectionID_VideoSwap;
        }
        else if (g.IsImageToVideo)
        {
            return T2IParamInput.SectionID_Video;
        }
        return T2IParamInput.SectionID_BaseOnly;
    }

    /// <summary>Returns the scheduler that samplers in the given section will use, mirroring how the workflow generator picks it.</summary>
    public static string SchedulerFor(T2IParamInput input, int section)
    {
        string scheduler = input.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: section, includeBase: false);
        if (scheduler is null && section == T2IParamInput.SectionID_Refiner)
        {
            scheduler = input.Get(ComfyUIBackendExtension.RefinerSchedulerParam, null);
        }
        return scheduler ?? input.Get(ComfyUIBackendExtension.SchedulerParam, "");
    }

    /// <summary>Walks up a model connection chain to find the sigma list of the nearest SetManualSigmas node, or null if none is found.</summary>
    public static string FindUpstreamSigmas(WorkflowGenerator g, JArray model)
    {
        while (model is not null && g.Workflow[$"{model[0]}"] is JObject node)
        {
            if ($"{node["class_type"]}" == "SetManualSigmas")
            {
                return $"{node["inputs"]["sigmas"]}";
            }
            model = node["inputs"]?["model"] as JArray;
        }
        return null;
    }

    /// <summary>Gets the sigma list for the given section, or throws a readable error if it is missing or invalid.</summary>
    public static List<double> GetSigmas(T2IParamInput input, int section)
    {
        string raw = input.Get(ManualSigmas, null, sectionId: section, includeBase: false);
        if (raw is null && section == T2IParamInput.SectionID_Refiner)
        {
            raw = input.Get(RefinerManualSigmas, null);
        }
        raw ??= input.Get(ManualSigmas, null);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SwarmUserErrorException("Scheduler is set to 'Manual', but no sigma list was given (Manual Sigmas, or Refiner Manual Sigmas for the refiner).");
        }
        return ParseSigmas(raw);
    }

    /// <summary>Parses a sigma list, or throws a readable error if it is invalid.</summary>
    public static List<double> ParseSigmas(string raw)
    {
        List<double> result = [];
        foreach (string part in raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                throw new SwarmUserErrorException($"Manual Sigmas: '{part}' is not a valid number (use '.' for decimals).");
            }
            result.Add(val);
        }
        if (result.Count < 2)
        {
            throw new SwarmUserErrorException("Manual Sigmas needs at least 2 values (eg '1, 0').");
        }
        return result;
    }
}
