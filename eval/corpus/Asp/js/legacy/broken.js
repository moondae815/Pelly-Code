// legacy popup helper (left over from an old editor, never parsed cleanly)
function oldPopup(url) {
    var w = window.open(url, "popup", "width=400,height=300");
    if (w) {
        w.focus();
    }
}
}
