/* 주문 목록 스크립트 */
var Order = {
    search: function () {
        fnSearch();
    }
};

// 검색 조건을 모아서 서버로 보낸다
function fnSearch() {
    var from = $("#fromDate").val();
    var to = $("#toDate").val();
    var keyword = $("#keyword").val();
    if (!isEmpty(from) && !checkDate(from)) {
        return;
    }
    if (!isEmpty(to) && !checkDate(to)) {
        return;
    }
    var params = {
        from: from,
        to: to,
        keyword: keyword
    };
    $.ajax({
        url: "/admin/order/list.asp",
        type: "POST",
        data: params,
        dataType: "html",
        success: function (html) {
            $("#listArea").html(html);
        },
        error: function () {
            alert("조회 중 오류가 발생했습니다.");
        }
    });
}

$(function () {
    $("#btnSearch").click(function () {
        fnSearch();
    });
    $("#btnReset").click(function () {
        $("#searchForm")[0].reset();
    });
    $("#keyword").keydown(function (e) {
        if (e.keyCode == 13) {
            fnSearch();
            return false;
        }
    });
    $(".btn_detail").bind("click", function () {
        gfnOpenPopup("/admin/order/detail.asp?no=" + $(this).attr("data-no"), 600, 400);
    });
});
