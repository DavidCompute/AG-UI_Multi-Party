using AguiGroupChat.Hub.Persistence.Relational;
using MySqlConnector;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// MySQL 存储集成测试：需要本地 / 容器内可用的 MySQL 8.0.13+ 测试库（默认连接 agui_test）。
/// 连接串可用环境变量 <c>AGUI_MYSQL_TEST_CONN</c> 覆盖；未配置 / 不可达时全部跳过。
/// 启动示例：
///   docker run -d --name agui-mysql-test -e MYSQL_ROOT_PASSWORD=root -e MYSQL_DATABASE=agui_test -p 3306:3306 mysql:8
/// 运行：dotnet test --filter "Category=MySql"
/// </summary>
[Trait("Category", "MySql")]
public sealed class MySqlRelationalStoreTests : RelationalStoreTestsBase
{
    private static readonly bool MySqlAvailable;
    private static readonly string MySqlConnectionString;

    static MySqlRelationalStoreTests()
    {
        MySqlConnectionString = Environment.GetEnvironmentVariable("AGUI_MYSQL_TEST_CONN")
            ?? "Server=localhost;Port=3306;Database=agui_test;User ID=root;Password=root";
        try
        {
            using var probe = new MySqlConnection(MySqlConnectionString);
            probe.Open();
            MySqlAvailable = true;
        }
        catch
        {
            MySqlAvailable = false;
        }
    }

    protected override string ProviderName => "mysql";
    protected override string ProviderConnectionString => MySqlConnectionString;
    protected override bool ProviderAvailable => MySqlAvailable;
    protected override RelationalStore CreateStore(string connectionString) => new MySqlStore(connectionString);

    protected override void ResetTables(RelationalStore db)
        => db.ExecuteScript("""
            TRUNCATE TABLE agui_sections;
            TRUNCATE TABLE agui_agent_registrations;
            TRUNCATE TABLE agui_users;
            TRUNCATE TABLE agui_messages;
            TRUNCATE TABLE agui_topics;
            TRUNCATE TABLE agui_group_members;
            TRUNCATE TABLE agui_groups;
            """);
}
