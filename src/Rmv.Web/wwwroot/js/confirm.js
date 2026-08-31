// "Are you sure?" for a form that destroys something.
//
// One listener for the whole site, reading the message out of a data attribute.
// It used to be onsubmit="return confirm('Delete @name?')" in six places, which
// put a member-supplied name inside JavaScript source. Razor cannot make that
// safe: it encodes the apostrophe to &#x27;, the HTML parser decodes it back
// before the script is compiled, and the string ends early. An alias of
//
//     ');alert(1)//
//
// therefore ran in an admin's browser on the members page. Read as an attribute
// the same text is a string and nothing else, there is no script for it to end,
// and the content policy no longer has to allow inline script at all.
//
// Capturing, so it runs before any other submit handler decides to send the form.
document.addEventListener('submit', event => {
    const message = event.target instanceof HTMLFormElement
        ? event.target.dataset.confirm
        : null;

    if (message && !window.confirm(message)) {
        event.preventDefault();
        event.stopPropagation();
    }
}, true);
