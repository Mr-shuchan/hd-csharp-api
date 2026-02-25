using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using SharpAstrology.HumanDesign.BlazorComponents;
using System.Collections.Generic;
using System;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// 1. 配置跨域，允许你的 Vue 前端访问
builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// 2. 注入 Blazor 服务端渲染所需的服务
builder.Services.AddLogging();
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(); // 防止组件缺少多语言服务报错

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => "C# SharpAstrology API is running! (Reflection Mode Active)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    
    try {
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        
        // 【核心黑科技】：使用反射动态获取组件需要的图表参数类型
        var componentType = typeof(HumanDesignGraph);
        var chartProp = componentType.GetProperty("Chart") ?? 
                        componentType.GetProperties().FirstOrDefault(p => p.Name.Contains("Chart"));
                        
        if (chartProp == null) {
            throw new Exception("在组件中找不到 Chart 属性。可用的属性有: " + string.Join(", ", componentType.GetProperties().Select(p => p.Name)));
        }

        var chartType = chartProp.PropertyType;
        object chartInstance;
        
        try {
            // 动态创建这个对象，绕过编译期的硬编码检查
            var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            chartInstance = Activator.CreateInstance(chartType, parsedDate);
        } catch (Exception ex) {
            // 如果缺少依赖(比如星历表)，这里会把需要的构造函数结构打印出来
            var ctors = chartType.GetConstructors().Select(c => c.ToString());
            throw new Exception($"无法实例化 {chartType.Name}。需要的构造函数: {string.Join(" | ", ctors)}。内部报错: {ex.Message}");
        }

        // 渲染精美图表组件
        var chartHtmlString = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            // 将动态创建好的 chart 对象传递给 Blazor 组件
            var dictionary = new Dictionary<string, object>
            {
                { chartProp.Name, chartInstance } 
            };
            var parameters = ParameterView.FromDictionary(dictionary);
            
            var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);
            return output.ToHtmlString();
        });

        return Results.Json(new {
            success = true,
            message = "图表渲染成功！",
            data = new {
                name = data.Name,
                chartImageSVG = chartHtmlString 
            }
        });
    } 
    catch (Exception ex) {
        // 将详尽的堆栈错误直接发给前端展示
        return Results.Json(new { success = false, message = ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
