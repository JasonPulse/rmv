// Shows the fields the picked game actually needs.
//
// The manual fields are in the markup and visible by default, so the form works
// with this file blocked or still loading. All this does is take them away for a
// game whose herald can answer, and reveal the herald-specific hint. The server
// decides the same way, from the game rather than from anything the form sent, so
// the two cannot disagree.
(() => {
    const picker = document.querySelector('[data-gamepicker]');
    if (!picker) {
        return;
    }

    const groups = { herald: [], manual: [] };
    document.querySelectorAll('[data-when]').forEach(el => groups[el.dataset.when]?.push(el));

    const sync = () => {
        // Nothing picked yet counts as manual, matching what the no-script form
        // shows, so the fields do not appear and vanish on first load.
        const herald = picker.selectedOptions[0]?.dataset.herald === 'true';
        groups.herald.forEach(el => el.hidden = !herald);
        groups.manual.forEach(el => el.hidden = herald);
    };

    picker.addEventListener('change', sync);
    sync();
})();
