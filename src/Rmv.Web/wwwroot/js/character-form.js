// Shows the fields the picked game actually needs.
//
// The manual fields and the lookup choice are in the markup and visible by
// default, so the form works with this file blocked or still loading. All this
// does is take away what the picked game does not need. The server decides the
// same way, from the game and its adapter rather than from anything the form
// sent, so the two cannot disagree.
//
// Three cases, not two:
//
//   No herald. Type the sheet in. No choice to make.
//   A herald that lists everyone. Look it up. No choice to make.
//   A herald that does not, which today is the Armory. The member chooses, and
//   the note beside the checkbox says why the choice exists.
(() => {
    const picker = document.querySelector('[data-gamepicker]');
    if (!picker) {
        return;
    }

    const groups = { herald: [], manual: [], choice: [] };
    document.querySelectorAll('[data-when]').forEach(el => groups[el.dataset.when]?.push(el));

    const toggle = document.querySelector('[data-heraldtoggle]');
    const notes = Array.from(document.querySelectorAll('[data-note-for]'));

    const sync = () => {
        const picked = picker.selectedOptions[0];

        // Nothing picked yet counts as manual, matching what the no-script form
        // shows, so the fields do not appear and vanish on first load.
        const hasHerald = picked?.dataset.herald === 'true';
        const optional = hasHerald && picked?.dataset.optional === 'true';

        // Unticking only means anything where the choice is offered.
        const lookUp = hasHerald && (!optional || (toggle?.checked ?? true));

        groups.herald.forEach(el => el.hidden = !lookUp);
        groups.manual.forEach(el => el.hidden = lookUp);
        groups.choice.forEach(el => el.hidden = !optional);

        // The note belongs to one game, so only that game's is shown.
        notes.forEach(el => el.hidden = el.dataset.noteFor !== picked?.value);
    };

    picker.addEventListener('change', sync);
    toggle?.addEventListener('change', sync);
    sync();
})();
