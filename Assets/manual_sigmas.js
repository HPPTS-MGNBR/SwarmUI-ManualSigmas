/** Helper that only shows each sigmas parameter when its scheduler is set to 'manual'. */
class ManualSigmasHelper {

    constructor() {
        // Maps each sigmas param ID to the scheduler param ID that controls it
        this.sigmaParamSchedulers = {
            'manualsigmas': 'scheduler',
            'refinermanualsigmas': 'refinerscheduler'
        };
    }

    /** Returns true if the given scheduler param is enabled and set to 'manual'. */
    isManual(paramId) {
        let elem = document.getElementById(`input_${paramId}`);
        if (!elem) {
            return false;
        }
        let toggler = document.getElementById(`input_${paramId}_toggle`);
        if (toggler && !toggler.checked) {
            return false;
        }
        return elem.value == 'manual';
    }

    /** Hides each sigmas parameter whose scheduler is not set to 'manual'. Runs after the core param visibility pass. */
    updateVisibility() {
        for (let sigmasId in this.sigmaParamSchedulers) {
            let elem = document.getElementById(`input_${sigmasId}`);
            if (elem && !this.isManual(this.sigmaParamSchedulers[sigmasId])) {
                findParentOfClass(elem, 'auto-input').style.display = 'none';
            }
        }
    }

    /** Listens for scheduler changes on freshly built param inputs. */
    attachListeners() {
        for (let sigmasId in this.sigmaParamSchedulers) {
            let schedulerId = this.sigmaParamSchedulers[sigmasId];
            for (let id of [`input_${schedulerId}`, `input_${schedulerId}_toggle`]) {
                let elem = document.getElementById(id);
                if (elem) {
                    elem.addEventListener('change', () => {
                        scheduleParamUnsupportUpdate();
                    });
                }
            }
        }
    }
}

manualSigmasHelper = new ManualSigmasHelper();
postParamBuildSteps.push(() => {
    manualSigmasHelper.attachListeners();
});
hideParamCallbacks.push(() => {
    manualSigmasHelper.updateVisibility();
});
