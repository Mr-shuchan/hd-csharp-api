using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using SharpAstrology.HumanDesign;
using SharpAstrology.HumanDesign.BlazorComponents;
using System.Collections.Generic;
using System;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// 注入额外服务以防止组件因缺少基础运行环境而报 NullReference
builder.Services.AddLogging();
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(); // 许多开源 UI 库依赖本地化服务

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => "C# SharpAstrology API is running! (Debug Mode)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        // 构建排盘数据
        var chart = new HumanDesignChart(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        
        var chartHtmlString = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            // 如果组件要求其他名称，请在这里更改 "Chart" 为正确的参数名
            var dictionary = new Dictionary<string, object?> { { "Chart", chart } };
            var parameters = ParameterView.FromDictionary(dictionary);
            
            // 渲染组件
            var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);
            return output.ToHtmlString();
        });

        return Results.Json(new {
            success = true,
            message = "图表渲染成功！",
            data = new { name = data.Name, chartImageSVG = chartHtmlString }
        });
    } 
    catch (Exception ex) {
        // 【核心修改】：使用 ex.ToString() 输出完整的堆栈信息，精准定位崩溃行！
        return Results.Json(new { success = false, message = ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
