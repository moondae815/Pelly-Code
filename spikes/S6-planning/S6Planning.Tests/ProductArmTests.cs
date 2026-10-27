using System.Text.Json.Nodes;
using S6Planning;

public class ProductArmTests
{
    [Fact]
    public void Scopes_map_the_Sql_shortcut_to_Sql_objects()
    {
        Assert.Equal(["Legacy", "Sql/objects"], ProductArm.ProductScopes(["Legacy", "Sql"]));
        Assert.Equal(["Modern"], ProductArm.ProductScopes(["Modern"]));
        Assert.Equal(["Sql/objects"], ProductArm.ProductScopes(["Sql"]));
    }

    [Fact]
    public void Final_task_lines_are_the_task_lines_under_the_tasks_heading_only()
    {
        const string doc = "---\nid: x\nstatus: draft\n---\n# t\n\n## 목표\n- [ ] T9 cs Not/A.cs#Task not a task here\n\n## 수용 기준\n- AC1: …\n\n## 작업\n"
            + "<!-- spec check: 오류 1, 경고 0\n  오류 X: y\n-->\n"
            + "- [ ] T1 cs A.cs#M do it [G1]\n- [ ] T3 cs B.cs#N T1의 변경에 맞춰 이 코드를 고친다 (영향 탐색으로 추가) [G1]\n- [ ] T2 sql S.sql change\n";
        Assert.Equal(
            "- [ ] T1 cs A.cs#M do it [G1]\n- [ ] T3 cs B.cs#N T1의 변경에 맞춰 이 코드를 고친다 (영향 탐색으로 추가) [G1]\n- [ ] T2 sql S.sql change\n",
            ProductArm.FinalTaskLines(doc));
    }

    [Fact]
    public void Record_keeps_the_raw_shape_so_score_reads_it_and_adds_the_extras()
    {
        var raw = new RawRecord("c", "P", 2, "d", 10, "think", true, "- [ ] T1 cs A.cs#M x\n", null, null, 1.5, "g");
        var json = ProductArm.ToJson(raw, new JsonObject { ["impactAdded"] = 1, ["dropped"] = 0 });

        var back = RawRecord.FromJson(json);
        Assert.Equal(raw, back);
        var node = JsonNode.Parse(json)!;
        Assert.Equal(1, (int)node["impactAdded"]!);
        Assert.Equal(0, (int)node["dropped"]!);
    }
}
