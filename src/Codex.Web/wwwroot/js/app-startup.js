(function () {
    // === 1. Status Message Cycling (Loading Splash) ===
    const messages = [
        "Inscribing the Grimoire...",
        "Loading D&D 5E Plugin...",
        "Tuning the FFG Force Dice...",
        "Scribing NPC Archetypes...",
        "Synchronizing with the Weave...",
        "Opening the Vaults...",
        "Polishing the Dice..."
    ];

    let index = 0;
    const statusEl = document.getElementById('status');

    function cycleStatus() {
        if (!statusEl) return;
        statusEl.classList.remove('active');
        
        setTimeout(() => {
            statusEl.innerText = messages[index];
            statusEl.classList.add('active');
            index = (index + 1) % messages.length;
        }, 500);
    }

    // === 2. Splash Screen Removal ===
    function removeSplash() {
        const el = document.getElementById("boot-splash");
        if (!el) return;
        el.style.opacity = '0';
        el.style.transition = 'opacity 0.5s ease';
        setTimeout(function() {
            if (el.parentNode) {
                el.parentNode.removeChild(el);
            }
        }, 500);
    }

    // === 3. Reconnect Modal Logic ===
    function initReconnectLogic() {
        const reconnectModal = document.getElementById("components-reconnect-modal");
        const retryButton = document.getElementById("components-reconnect-button");
        const resumeButton = document.getElementById("components-resume-button");

        if (!reconnectModal) return;

        reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
        
        if (retryButton) {
            retryButton.addEventListener("click", retry);
        }
        
        if (resumeButton) {
            resumeButton.addEventListener("click", resume);
        }

        function handleReconnectStateChanged(event) {
            if (event.detail.state === "show") {
                if (reconnectModal.showModal) reconnectModal.showModal();
            } else if (event.detail.state === "hide") {
                if (reconnectModal.close) reconnectModal.close();
            } else if (event.detail.state === "failed") {
                document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            } else if (event.detail.state === "rejected") {
                location.reload();
            }
        }

        async function retry() {
            document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            try {
                const successful = await Blazor.reconnect();
                if (!successful) {
                    const resumeSuccessful = await Blazor.resumeCircuit();
                    if (!resumeSuccessful) {
                        location.reload();
                    } else if (reconnectModal.close) {
                        reconnectModal.close();
                    }
                }
            } catch (err) {
                document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            }
        }

        async function resume() {
            try {
                const successful = await Blazor.resumeCircuit();
                if (!successful) {
                    location.reload();
                }
            } catch {
                location.reload();
            }
        }

        async function retryWhenDocumentBecomesVisible() {
            if (document.visibilityState === "visible") {
                await retry();
            }
        }
    }

    // === 4. Startup Orchestration ===
    function init() {
        // Initialize Lucide icons if available
        if (window.lucide) {
            lucide.createIcons();
        }

        // Start status cycling
        if (statusEl) {
            statusEl.classList.add('active');
            setInterval(cycleStatus, 2500);
        }

        // Initialize Reconnect Modal logic
        initReconnectLogic();

        // Boot Blazor
        if (window.Blazor) {
            try {
                Blazor.start().then(removeSplash).catch(removeSplash);
            } catch (e) {
                removeSplash();
            }
        } else {
            // Fallback if Blazor is missing for some reason
            setTimeout(removeSplash, 2000);
        }

        // Fallback for splash removal
        window.addEventListener("load", function () {
            setTimeout(removeSplash, 8000);
        });
    }

    // Run when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
