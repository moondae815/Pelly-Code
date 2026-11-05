/* common.js - 공통 스크립트 (관리자 사이트) */
function isEmpty(v) {
    if (v == null) return true;
    return String(v).replace(/^\s+|\s+$/g, "") == "";
}

/* 날짜 형식 검사: yyyy-mm-dd */
function checkDate(v) {
    var re = /^(\d{4})-(\d{2})-(\d{2})$/;
    var m = re.exec(v);
    if (!m) {
        alert("날짜 형식이 올바르지 않습니다. (예: 2003-05-01)");
        return false;
    }
    return true;
}

/* 천 단위 쉼표 */
function formatNumber(n) {
    var s = String(n);
    while (/(\d+)(\d{3})/.test(s)) {
        s = s.replace(/(\d+)(\d{3})/, "$1,$2");
    }
    return s;
}
