// member edit form
function fnSave() {
    var f = document.memberForm;
    if (f.memberName.value == "") {
        alert("Enter the name.");
        return false;
    }
    f.submit();
    return true;
}