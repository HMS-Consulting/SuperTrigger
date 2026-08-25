(function () {
    const idleTimeoutMs = 30 * 60 * 1000; // 30 minutes of no user interaction
    const logoutUrl = '/account/logout';
    let idleTimer;

    function resetTimer() {
        clearTimeout(idleTimer);
        idleTimer = setTimeout(() => {
            window.location.href = logoutUrl;
        }, idleTimeoutMs);
    }

    ['mousemove', 'mousedown', 'keydown', 'scroll', 'touchstart', 'click'].forEach(evt =>
        document.addEventListener(evt, resetTimer, { passive: true }));

    resetTimer();
})();
