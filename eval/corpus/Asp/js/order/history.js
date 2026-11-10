// order history (W3 corpus: one function, called from a .js file, a page block and an onclick)
function fnHistory(no) {
    $.ajax({
        url: "/admin/order/history.asp",
        type: "POST",
        data: { no: no },
        success: function (html) {
            $("#historyArea").html(html);
        }
    });
}

function fnReload() {
    fnHistory(gNo);
}
