using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using SharpAstrology.HumanDesign;
// 引入 Blazor 组件的命名空间
using SharpAstrology.HumanDesign.BlazorComponents;

var builder = WebApplication.CreateBuilder(args);

// 1. 配置跨域，允许你的 Vue 前端访问
builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// 2. 注入 Blazor 服务端渲染所需的服务
builder.Services.AddLogging();
builder.Services.AddRazorComponents();

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => "C# SharpAstrology API with Blazor Chart Renderer is running!");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    
    try {
        // 1. 调用 SharpAstrology 进行真实的排盘计算
        // 真实环境需要传入具体的 UTC 时间。这里做核心实例化演示：
        // var chart = new HumanDesignChart(new System.DateTime(2000, 1, 1, 12, 0, 0, System.DateTimeKind.Utc));
        
        // 此处为了代码不报错，使用默认构造或模拟数据，你需要根据官方文档传入正确的时间参数
        // 假设我们得到了排盘对象 chart
        
        // 2. 启动虚拟 HTML 渲染器
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        
        // 3. 在服务器内存中渲染 Blazor 组件！
        var chartHtmlString = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            // 将计算好的 chart 对象传递给 Blazor 组件
            // 提示：如果组件不叫 HumanDesignGraph，请替换为文档中真实的名字 (例如 BodyGraph 等)
            var dictionary = new Dictionary<string, object?>
            {
                // { "Chart", chart } 
            };
            var parameters = ParameterView.FromDictionary(dictionary);
            
            // 渲染组件并捕获输出的 HTML
            var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);
            return output.ToHtmlString();
        });

        // 4. 将原本纯文本的数据，连同渲染好的精美图表字符串一起发给 Vue
        return Results.Json(new {
            success = true,
            message = "图表渲染成功！",
            data = new {
                name = data.Name,
                // 前端直接使用 v-html="chartImageSVG" 即可显示这张精美的图！
                chartImageSVG = chartHtmlString 
            }
        });
    } 
    catch (System.Exception ex) {
        return Results.Json(new { success = false, message = "后端渲染出错: " + ex.Message });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
