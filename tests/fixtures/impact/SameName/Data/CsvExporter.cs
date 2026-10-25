namespace SameName.Data;

public class CsvExporter : BaseExporter
{
    public override string Export(Order order) => "csv";
}
