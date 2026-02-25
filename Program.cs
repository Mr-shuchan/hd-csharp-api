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

// 配置跨域
builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// 注入基础服务
builder.Services.AddLogging();
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(); 

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => "C# SharpAstrology API is running! (Smart Reflection Mode Active)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    
    try {
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        
        // 1. 获取组件及需要的参数类型 (IHumanDesignChart)
        var componentType = typeof(HumanDesignGraph);
        var chartProp = componentType.GetProperty("Chart") ?? 
                        componentType.GetProperties().FirstOrDefault(p => p.Name.Contains("Chart"));
                        
        if (chartProp == null) throw new Exception("在组件中找不到 Chart 属性。");

        var chartInterfaceType = chartProp.PropertyType;
        
        // 2. 【核心修复】：扫描所有加载的代码库，找到实现了该接口的具体类 (Class)
        var concreteChartType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } 
                catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t => chartInterfaceType.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);

        if (concreteChartType == null) {
            throw new Exception($"严重错误：无法在依赖库中找到实现了 {chartInterfaceType.Name} 的具体排盘类！");
        }

        // 3. 实例化具体的排盘类
        object chartInstance;
        try {
            var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            chartInstance = Activator.CreateInstance(concreteChartType, parsedDate);
        } catch (Exception ex) {
            var ctors = concreteChartType.GetConstructors().Select(c => c.ToString());
            throw new Exception($"找到具体类 {concreteChartType.FullName}，但其实例化失败。需要的构造函数: {string.Join(" | ", ctors)}。报错详情: {ex.InnerException?.Message ?? ex.Message}");
        }

        // 4. 将生成的实例丢给 Blazor 渲染精美图表
        var chartHtmlString = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
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
        return Results.Json(new { success = false, message = ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
