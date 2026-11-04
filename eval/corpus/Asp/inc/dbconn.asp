<%
'--------------------------------------------------------------------
' Database connection. The connection string is a placeholder only.
'--------------------------------------------------------------------
Const DB_CONNECTION_STRING = "Provider=SQLOLEDB;Data Source=db.example.invalid;Initial Catalog=PellyCorpus;Integrated Security=SSPI;"

Dim DbConn

Sub OpenDb()
    Set DbConn = Server.CreateObject("ADODB.Connection")
    DbConn.Open DB_CONNECTION_STRING
End Sub

Sub CloseDb()
    If IsObject(DbConn) Then
        If Not DbConn Is Nothing Then
            If DbConn.State = adStateOpen Then DbConn.Close
            Set DbConn = Nothing
        End If
    End If
End Sub
%>
