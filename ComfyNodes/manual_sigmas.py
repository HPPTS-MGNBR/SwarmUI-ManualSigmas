import copy
import re
import torch
import comfy.samplers


def manual_scheduler(model_sampling, steps):
    sigmas = getattr(model_sampling, "manual_sigmas", None)
    if sigmas is None:
        raise ValueError("Scheduler 'manual' requires the model to pass through a SetManualSigmas node.")
    # dpm_2 / uni_pc samplers request one extra step then drop the penultimate sigma, so pad to keep the user's list exact
    if len(sigmas) == steps:
        sigmas = sigmas[:-1] + sigmas[-2:]
    elif len(sigmas) != steps + 1:
        raise ValueError(f"Manual sigmas has {len(sigmas)} values ({len(sigmas) - 1} steps), but the sampler is set to {steps} steps.")
    return torch.FloatTensor(sigmas)


def register_manual_scheduler():
    comfy.samplers.SCHEDULER_HANDLERS["manual"] = comfy.samplers.SchedulerHandler(manual_scheduler)
    # Some node packs (eg RES4LYF) rebind these to separate list copies, and KSampler silently falls back to the first scheduler if its own list lacks ours
    for names in [comfy.samplers.SCHEDULER_NAMES, comfy.samplers.KSampler.SCHEDULERS]:
        if "manual" not in names:
            names.append("manual")


register_manual_scheduler()


class SetManualSigmas:
    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "model": ("MODEL",),
                "sigmas": ("STRING", {"default": "1, 0.5, 0"}),
            }
        }

    CATEGORY = "sampling"
    RETURN_TYPES = ("MODEL",)
    FUNCTION = "apply"
    DESCRIPTION = "Attaches a list of sigmas to the model, used by any sampler whose scheduler is set to 'manual'."

    def apply(self, model, sigmas):
        values = [float(v) for v in re.split(r"[\s,;]+", sigmas.strip()) if v]
        # Re-register in case another node pack rebound the scheduler lists after this file loaded
        register_manual_scheduler()
        m = model.clone()
        model_sampling = copy.copy(m.get_model_object("model_sampling"))
        model_sampling.manual_sigmas = values
        m.add_object_patch("model_sampling", model_sampling)
        return (m,)


NODE_CLASS_MAPPINGS = {
    "SetManualSigmas": SetManualSigmas,
}
