let pointerX = 0;
let pointerY = 0;
let framePending = false;

window.addEventListener("pointermove", event => {
    pointerX = event.clientX;
    pointerY = event.clientY;

    if (framePending) {
        return;
    }

    framePending = true;
    requestAnimationFrame(() => {
        document.documentElement.style.setProperty("--cursor-x", `${pointerX}px`);
        document.documentElement.style.setProperty("--cursor-y", `${pointerY}px`);
        framePending = false;
    });
}, { passive: true });
