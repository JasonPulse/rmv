// The edit-in-place behaviour of the history editor.
//
// In a file rather than inline so the content policy can refuse inline script
// outright; see SecurityHeaders. Nothing here reads anything but data
// attributes Razor has already encoded.

// Clicking edit fills a form rather than navigating. Values come from data
// attributes Razor has already HTML-encoded.
//
// Each form doubles as its own add form, so the hidden Id is what decides insert
// against update. Getting stuck in edit mode with no way out means the next save
// silently overwrites the row you were last looking at, which is why cancel is
// not optional polish.
//
// Clearing needs explicit blanks, not empty strings for everything. Assigning ''
// to a <select> with no matching option leaves selectedIndex at -1, so the field
// posts nothing at all, and '' in a number input fails its Range check. Both of
// those turned a cancelled edit into a form that could not be submitted.
const FORMS = {
    game: {
        prefix: 'Input',
        adding: 'Adding a new game.',
        focus: 'Game',
        name: d => d.game,
        fields: {
            Id: { attr: 'id', blank: '' },
            Game: { attr: 'game', blank: '' },
            Guilds: { attr: 'guilds', blank: '' },
            Period: { attr: 'period', blank: '' },
            SortOrder: { attr: 'sort', blank: '0' },
            HeraldAdapterKey: { attr: 'adapter', blank: '' },
            HeraldBaseUrl: { attr: 'heraldurl', blank: '' },
        },
        checks: { IsActive: { attr: 'active', blank: false } },
    },
    link: {
        prefix: 'LinkInput',
        adding: 'Adding a new link.',
        focus: 'Label',
        name: d => d.label || 'this link',
        fields: {
            Id: { attr: 'id', blank: '' },
            GamePresenceId: { attr: 'game', blank: '' },
            Kind: { attr: 'kind', blank: '0' },
            Label: { attr: 'label', blank: '' },
            Url: { attr: 'url', blank: '' },
            SortOrder: { attr: 'sort', blank: '0' },
        },
        checks: {},
    },
};

function field(form, key) {
    return document.querySelector('#' + FORMS[form].prefix + '_' + key);
}

function setMode(form, editing) {
    const hint = document.querySelector(`[data-mode="${form}"]`);
    const cancel = document.querySelector(`[data-cancel="${form}"]`);
    if (hint) hint.textContent = editing ? `Editing ${editing}. Saving overwrites it.` : FORMS[form].adding;
    if (cancel) cancel.hidden = !editing;
}

// dataset null means clear, so each field falls back to its own blank.
function fill(form, dataset) {
    const spec = FORMS[form];
    for (const [key, f] of Object.entries(spec.fields)) {
        const el = field(form, key);
        if (el) el.value = dataset?.[f.attr] ?? f.blank;
    }
    for (const [key, f] of Object.entries(spec.checks)) {
        const el = field(form, key);
        if (el) el.checked = dataset ? dataset[f.attr] === 'true' : f.blank;
    }
}

function bindEdit(selector, form) {
    document.querySelectorAll(selector).forEach(a => a.addEventListener('click', e => {
        e.preventDefault();
        fill(form, a.dataset);
        setMode(form, FORMS[form].name(a.dataset));
        field(form, FORMS[form].focus)?.focus();
        document.querySelector('#' + form + '-form')
            ?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }));
}

bindEdit('[data-edit]', 'game');
bindEdit('[data-edit-link]', 'link');

document.querySelectorAll('[data-cancel]').forEach(b => b.addEventListener('click', () => {
    fill(b.dataset.cancel, null);
    setMode(b.dataset.cancel, null);
}));
