using PullPatrol.Api.Analysis;

namespace PullPatrol.Tests;

public class StaticRuleAnalyzerTests
{
    private static IReadOnlyList<Finding> Analyze(string path, params string[] addedLines)
    {
        var patch = $"@@ -0,0 +1,{addedLines.Length} @@\n" + string.Join("\n", addedLines.Select(l => "+" + l));
        var file = new ChangedFile(path, "added", DiffParser.Parse(patch));
        return new StaticRuleAnalyzer().Analyze([file]);
    }

    [Theory]
    [InlineData("src/Db.cs", "var apiKey = \"sk_live_1234567890abcdef\";", "PP001")]
    [InlineData("appsettings.json", "\"Password\": \"SuperSecret123!\"", "PP001")]
    [InlineData("deploy.sh", "export TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", "PP002")]
    [InlineData("id_rsa", "-----BEGIN RSA PRIVATE KEY-----", "PP003")]
    [InlineData("src/Repo.cs", "var sql = $\"SELECT * FROM Users WHERE Id = {id}\";", "PP004")]
    [InlineData("src/Repo.cs", "var sql = \"DELETE FROM Orders WHERE Id = \" + orderId;", "PP004")]
    [InlineData("src/Http.cs", "handler.ServerCertificateCustomValidationCallback = (a, b, c, d) => true;", "PP005")]
    [InlineData("src/Hash.cs", "using var md5 = MD5.Create();", "PP006")]
    [InlineData("web/app.ts", "element.innerHTML = userInput;", "PP007")]
    [InlineData("src/Service.cs", "var user = _repo.GetUserAsync(id).Result;", "PP101")]
    [InlineData("src/Service.cs", "var client = new HttpClient();", "PP102")]
    [InlineData("src/Service.cs", "public async void Save() {", "PP201")]
    [InlineData("src/Service.cs", "catch (Exception) { }", "PP202")]
    public void DetectsRiskyPatterns(string path, string line, string expectedRule)
    {
        var findings = Analyze(path, line);

        var finding = Assert.Single(findings, f => f.Source == expectedRule);
        Assert.Equal(1, finding.Line);
        Assert.Equal(path, finding.Path);
    }

    [Theory]
    [InlineData("src/Db.cs", "var apiKey = configuration[\"ApiKey\"];")]
    [InlineData("src/Repo.cs", "var users = await db.Users.Where(u => u.Id == id).ToListAsync();")]
    [InlineData("src/Repo.cs", "const string Sql = \"SELECT * FROM Users WHERE Id = @id\";")]
    [InlineData("src/Service.cs", "var user = await _repo.GetUserAsync(id);")]
    [InlineData("src/Service.cs", "// var user = _repo.GetUserAsync(id).Result;")]
    [InlineData("src/Form.cs", "private async void OnClick(object sender, EventArgs e) {")]
    [InlineData("web/app.py", "var client = new HttpClient();")] // C#-only rule on another language
    public void IgnoresSafeCode(string path, string line)
    {
        Assert.Empty(Analyze(path, line));
    }

    [Fact]
    public void OnlyAnalyzesAddedLines()
    {
        var patch = "@@ -1,2 +1,1 @@\n-var x = task.Result;\n var y = 1;";
        var file = new ChangedFile("src/A.cs", "modified", DiffParser.Parse(patch));

        Assert.Empty(new StaticRuleAnalyzer().Analyze([file]));
    }
}
