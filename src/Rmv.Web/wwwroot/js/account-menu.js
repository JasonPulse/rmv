// Account dropdown. Small enough not to want a framework, but a menu still has
// to behave: close on outside click, close on Escape and return focus, and be
// reachable from the keyboard.
(() => {
    const toggle = document.querySelector('[data-account-toggle]');
    const menu = document.querySelector('[data-account-menu]');
    if (!toggle || !menu) return;

    const items = () => [...menu.querySelectorAll('.menu__item')];

    function open() {
        menu.hidden = false;
        toggle.setAttribute('aria-expanded', 'true');
    }

    function close({ refocus = false } = {}) {
        menu.hidden = true;
        toggle.setAttribute('aria-expanded', 'false');
        if (refocus) toggle.focus();
    }

    const isOpen = () => !menu.hidden;

    toggle.addEventListener('click', e => {
        e.stopPropagation();
        isOpen() ? close() : open();
    });

    // Down-arrow from the toggle opens and lands on the first item, which is what
    // a menu button is expected to do.
    toggle.addEventListener('keydown', e => {
        if (e.key === 'ArrowDown' || e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            open();
            items()[0]?.focus();
        }
    });

    menu.addEventListener('keydown', e => {
        const list = items();
        const i = list.indexOf(document.activeElement);

        if (e.key === 'ArrowDown') {
            e.preventDefault();
            list[(i + 1) % list.length]?.focus();
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            list[(i - 1 + list.length) % list.length]?.focus();
        } else if (e.key === 'Home') {
            e.preventDefault();
            list[0]?.focus();
        } else if (e.key === 'End') {
            e.preventDefault();
            list[list.length - 1]?.focus();
        }
    });

    // Escape anywhere closes it and puts focus back on the toggle, so keyboard
    // users are not stranded inside a hidden subtree.
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape' && isOpen()) close({ refocus: true });
    });

    document.addEventListener('click', e => {
        if (isOpen() && !menu.contains(e.target) && e.target !== toggle) close();
    });

    // Focus leaving the cluster entirely closes it. Guarded on relatedTarget
    // because that is null when focus goes to the browser chrome.
    menu.addEventListener('focusout', e => {
        if (e.relatedTarget && !menu.contains(e.relatedTarget) && e.relatedTarget !== toggle) {
            close();
        }
    });
})();
