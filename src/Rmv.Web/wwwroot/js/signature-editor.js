// The signature editor: drag to place, type to change, save to draw.
//
// The design is one JSON document in a hidden field. This reads it on load, mutates
// it, and writes it back on every change, so what the form posts is exactly what the
// canvas shows. The server clamps all of it again on the way in and renders from the
// stored design, so nothing here is trusted and nothing here needs to be.
//
// Why the canvas is faithful. It is the render's own size, and the text is the same
// Vollkorn the renderer draws with, served as woff2 at the same pixel sizes. The 2014
// version could not do that: no webfonts, and GD's metrics were invisible to a
// browser, so it asked the server to rasterise every field as a transparent PNG and
// dragged the pictures. This drags text and costs the server nothing until Save.
(() => {
    const form = document.querySelector('[data-editor]');
    if (!form) {
        return;
    }

    const field = form.querySelector('[data-design]');
    const stage = form.querySelector('[data-stage]');
    const list = form.querySelector('[data-elements]');
    const counter = form.querySelector('[data-element-count]');
    const colour = form.querySelector('[data-colour]');

    const width = Number(form.dataset.canvasWidth);
    const height = Number(form.dataset.canvasHeight);
    const maxElements = Number(form.dataset.maxElements);

    let design;

    try {
        design = JSON.parse(field.value);
    } catch {
        design = null;
    }

    if (!design || !Array.isArray(design.elements)) {
        // A design that will not parse is not something to guess at. The page still
        // shows the last render and the address, which is most of why anybody is here.
        return;
    }

    // Which line the token buttons drop into.
    let focused = 0;

    const clamp = (n, low, high) => Math.min(Math.max(n, low), high);

    const write = () => {
        field.value = JSON.stringify(design);
    };

    // --- the canvas ------------------------------------------------------------

    const paint = () => {
        stage.style.backgroundColor = design.colour || '#0a0c12';
        stage.style.backgroundImage = backgroundUrl();
    };

    const backgroundUrl = () => {
        if (design.background === 'Preset' && design.backgroundKey) {
            const chosen = form.querySelector(
                `[data-background="preset"][data-key="${CSS.escape(design.backgroundKey)}"] img`);
            return chosen ? `url(${chosen.src})` : 'none';
        }

        if (design.background === 'Upload' && design.backgroundKey) {
            return `url(/tools/signature?handler=Background&id=${encodeURIComponent(design.backgroundKey)})`;
        }

        return 'none';
    };

    const draw = () => {
        stage.textContent = '';

        design.elements.forEach((element, index) => {
            const line = document.createElement('div');
            line.className = 'sig__line';
            line.dataset.line = String(index);
            line.tabIndex = 0;
            line.setAttribute('role', 'button');
            line.setAttribute('aria-label', `Line ${index + 1}: ${element.template}`);

            line.style.left = `${element.x}px`;
            line.style.top = `${element.y}px`;
            line.style.fontSize = `${element.size}px`;
            line.style.color = element.colour;

            if (element.outline) {
                // Four shadows rather than a stroke, which is what CSS has. The
                // renderer strokes the glyph outline properly; this is close enough
                // to judge placement by, and the real render is one Save away.
                const o = element.outline;
                line.style.textShadow =
                    `1px 0 0 ${o}, -1px 0 0 ${o}, 0 1px 0 ${o}, 0 -1px 0 ${o}`;
            }

            if (element.align === 'Centre') {
                line.style.transform = 'translateX(-50%)';
            } else if (element.align === 'Right') {
                line.style.transform = 'translateX(-100%)';
            }

            // The tokens are shown as typed here, because resolving them in the
            // browser would need every character's data and would be a second
            // implementation of SignatureTokens. Placement is what this is for.
            line.textContent = element.template || '(empty)';

            if (index === focused) {
                line.classList.add('sig__line--on');
            }

            stage.append(line);
        });
    };

    // --- dragging --------------------------------------------------------------

    let dragging = null;

    stage.addEventListener('pointerdown', event => {
        const line = event.target.closest('[data-line]');
        if (!line) {
            return;
        }

        const index = Number(line.dataset.line);
        focused = index;

        const box = stage.getBoundingClientRect();

        dragging = {
            index,
            line,
            // Where in the line the pointer went down, so it does not jump to the
            // cursor on the first move.
            grabX: event.clientX - box.left - design.elements[index].x,
            grabY: event.clientY - box.top - design.elements[index].y,
        };

        // Capture so the drag survives the pointer leaving the line. Wrapped
        // because a synthetic pointer has nothing to capture, and a failure here
        // must not stop the drag working.
        try {
            line.setPointerCapture(event.pointerId);
        } catch { /* no active pointer, which is a test or an odd browser */ }

        line.classList.add('sig__line--drag');

        // Highlight rather than redraw. Rebuilding the canvas here would remove the
        // node under the pointer, and a detached node receives no more of the drag,
        // so the line would stick to where it was picked up.
        highlight();
        show();
        event.preventDefault();
    });

    stage.addEventListener('pointermove', event => {
        if (!dragging) {
            return;
        }

        const box = stage.getBoundingClientRect();
        const element = design.elements[dragging.index];

        element.x = Math.round(clamp(event.clientX - box.left - dragging.grabX, 0, width));
        element.y = Math.round(clamp(event.clientY - box.top - dragging.grabY, 0, height));

        dragging.line.style.left = `${element.x}px`;
        dragging.line.style.top = `${element.y}px`;
    });

    // Marks which line is being edited, without touching the nodes themselves.
    const highlight = () => {
        stage.querySelectorAll('[data-line]').forEach(line => {
            line.classList.toggle('sig__line--on', Number(line.dataset.line) === focused);
        });
    };

    const endDrag = () => {
        if (!dragging) {
            return;
        }

        dragging.line.classList.remove('sig__line--drag');
        dragging = null;
        write();
        show();
    };

    stage.addEventListener('pointerup', endDrag);
    stage.addEventListener('pointercancel', endDrag);

    // Arrow keys, because a pointer is not the only way to place something and one
    // pixel of nudging is hard to drag.
    stage.addEventListener('keydown', event => {
        const line = event.target.closest('[data-line]');
        if (!line) {
            return;
        }

        const step = event.shiftKey ? 10 : 1;
        const element = design.elements[Number(line.dataset.line)];
        const moves = {
            ArrowLeft: [-step, 0],
            ArrowRight: [step, 0],
            ArrowUp: [0, -step],
            ArrowDown: [0, step],
        };

        if (!(event.key in moves)) {
            return;
        }

        const [dx, dy] = moves[event.key];
        element.x = Math.round(clamp(element.x + dx, 0, width));
        element.y = Math.round(clamp(element.y + dy, 0, height));

        focused = Number(line.dataset.line);
        write();
        draw();
        show();
        stage.querySelector(`[data-line="${focused}"]`)?.focus();
        event.preventDefault();
    });

    // --- the side panel --------------------------------------------------------

    const characters = JSON.parse(list.dataset.characters || '[]');
    const fonts = JSON.parse(list.dataset.fonts || '[]');

    const show = () => {
        list.textContent = '';

        design.elements.forEach((element, index) => {
            const row = document.createElement('div');
            row.className = 'sig__row';
            if (index === focused) {
                row.classList.add('sig__row--on');
            }

            row.append(text(element, index));
            row.append(controls(element, index));
            list.append(row);
        });

        counter.textContent =
            `${design.elements.length} of ${maxElements} lines.`;

        form.querySelector('[data-add]').disabled = design.elements.length >= maxElements;
    };

    const text = (element, index) => {
        const wrap = document.createElement('div');
        wrap.className = 'field';

        const input = document.createElement('input');
        input.className = 'input';
        input.type = 'text';
        input.value = element.template;
        input.maxLength = 160;
        input.setAttribute('aria-label', `Text of line ${index + 1}`);

        input.addEventListener('focus', () => { focused = index; draw(); });
        input.addEventListener('input', () => {
            element.template = input.value;
            write();
            draw();
        });

        wrap.append(input);
        return wrap;
    };

    const controls = (element, index) => {
        const bar = document.createElement('div');
        bar.className = 'sig__controls';

        bar.append(number('Size', element.size, 8, 48, value => { element.size = value; }));
        bar.append(swatch('Colour', element.colour, value => { element.colour = value; }));
        bar.append(toggle('Outline', element.outline, value => {
            element.outline = value ? '#000000' : null;
        }));

        bar.append(choose('Align', ['Left', 'Centre', 'Right'].map(a => [a, a]), element.align,
            value => { element.align = value; }));

        bar.append(choose('Font', fonts.map(f => [f, f]), element.font,
            value => { element.font = value; }));

        bar.append(choose(
            'Character',
            [['', 'None, just you']].concat(characters.map(c => [String(c.id), c.label])),
            element.characterId === null ? '' : String(element.characterId),
            value => { element.characterId = value === '' ? null : Number(value); }));

        const remove = document.createElement('button');
        remove.className = 'btn btn--ghost btn--small';
        remove.type = 'button';
        remove.textContent = 'Remove';
        remove.setAttribute('aria-label', `Remove line ${index + 1}`);
        remove.addEventListener('click', () => {
            design.elements.splice(index, 1);
            focused = Math.max(0, focused - 1);
            write();
            draw();
            show();
        });
        bar.append(remove);

        return bar;
    };

    // --- the little controls, each one label plus input -------------------------

    const labelled = (label, input) => {
        const wrap = document.createElement('label');
        wrap.className = 'sig__control';
        const span = document.createElement('span');
        span.textContent = label;
        wrap.append(span, input);
        return wrap;
    };

    const changed = () => { write(); draw(); };

    const number = (label, value, low, high, set) => {
        const input = document.createElement('input');
        input.className = 'input input--narrow';
        input.type = 'number';
        input.min = String(low);
        input.max = String(high);
        input.value = String(value);
        input.addEventListener('input', () => {
            set(clamp(Number(input.value) || low, low, high));
            changed();
        });
        return labelled(label, input);
    };

    const swatch = (label, value, set) => {
        const input = document.createElement('input');
        input.type = 'color';
        input.className = 'input input--narrow';
        input.value = value;
        input.addEventListener('input', () => { set(input.value); changed(); });
        return labelled(label, input);
    };

    const toggle = (label, value, set) => {
        const input = document.createElement('input');
        input.type = 'checkbox';
        input.checked = Boolean(value);
        input.addEventListener('change', () => { set(input.checked); changed(); });
        return labelled(label, input);
    };

    const choose = (label, options, value, set) => {
        const select = document.createElement('select');
        select.className = 'input';

        for (const [key, name] of options) {
            const option = document.createElement('option');
            option.value = key;
            option.textContent = name;
            option.selected = key === value;
            select.append(option);
        }

        select.addEventListener('change', () => { set(select.value); changed(); });
        return labelled(label, select);
    };

    // --- tokens and backgrounds ------------------------------------------------

    form.querySelectorAll('[data-token]').forEach(button => {
        button.addEventListener('click', () => {
            const element = design.elements[focused];
            if (!element) {
                return;
            }

            element.template = `${element.template}%${button.dataset.token}%`;
            write();
            draw();
            show();
        });
    });

    form.querySelectorAll('[data-background]').forEach(button => {
        button.addEventListener('click', () => {
            const kind = button.dataset.background;

            design.background = kind === 'preset' ? 'Preset' : kind === 'upload' ? 'Upload' : 'Colour';
            design.backgroundKey = kind === 'colour' ? null : button.dataset.key;

            write();
            paint();
        });
    });

    colour.value = design.colour || '#0a0c12';
    colour.addEventListener('input', () => {
        design.colour = colour.value;
        write();
        paint();
    });

    // Copy the embed line, since the whole point is pasting it somewhere else.
    document.querySelectorAll('[data-copy]').forEach(input => {
        input.addEventListener('focus', () => input.select());
        input.addEventListener('click', () => input.select());
    });

    form.querySelector('[data-add]').addEventListener('click', () => {
        if (design.elements.length >= maxElements) {
            return;
        }

        const last = design.elements[design.elements.length - 1];

        design.elements.push({
            x: 12,
            y: last ? clamp(last.y + 24, 0, height) : 18,
            align: 'Left',
            font: last ? last.font : 'vollkorn',
            size: 16,
            colour: last ? last.colour : '#e8d8a0',
            outline: last ? last.outline : null,
            characterId: last ? last.characterId : null,
            template: 'New line',
        });

        focused = design.elements.length - 1;
        write();
        draw();
        show();
    });

    paint();
    draw();
    show();
})();
