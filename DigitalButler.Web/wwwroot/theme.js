(function () {
    const storageKey = "digital-butler-theme";
    const modes = new Set(["light", "dark", "system"]);
    const systemQuery = window.matchMedia("(prefers-color-scheme: dark)");

    function readMode() {
        try {
            const stored = window.localStorage.getItem(storageKey);
            return modes.has(stored) ? stored : "system";
        } catch {
            return "system";
        }
    }

    function resolveMode(mode) {
        return mode === "dark" || (mode === "system" && systemQuery.matches)
            ? "dark"
            : "light";
    }

    function applyTheme(mode) {
        const nextMode = modes.has(mode) ? mode : "system";
        const resolved = resolveMode(nextMode);
        const root = document.documentElement;

        root.dataset.theme = resolved;
        root.dataset.themePreference = nextMode;
        root.style.colorScheme = resolved;

        return { mode: nextMode, resolved };
    }

    function setTheme(mode) {
        const nextMode = modes.has(mode) ? mode : "system";
        try {
            window.localStorage.setItem(storageKey, nextMode);
        } catch {
            // Storage can be unavailable in private or locked-down contexts.
        }

        return applyTheme(nextMode);
    }

    function getTheme() {
        return applyTheme(readMode());
    }

    function handleSystemChange() {
        if (readMode() === "system") {
            applyTheme("system");
        }
    }

    if (systemQuery.addEventListener) {
        systemQuery.addEventListener("change", handleSystemChange);
    } else if (systemQuery.addListener) {
        systemQuery.addListener(handleSystemChange);
    }

    window.digitalButlerTheme = {
        getTheme,
        setTheme,
        initialize: getTheme
    };

    getTheme();
})();
