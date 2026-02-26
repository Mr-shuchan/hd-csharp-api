using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

// 【绝杀线索】：根据前端堆栈提供的精准命名空间，直接引入官方类！
using SharpAstrology.DataModels;
using SharpAstrology.Interfaces;
using SharpAstrology.Enums;
using SharpAstrology.Ephemerides;
using SharpAstrology.HumanDesign.BlazorComponents;

using System;
using System.Collections.Generic;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

// 配置跨域
builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddLogging();
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(); 

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => "C# SharpAstrology API is running! (Clean Strongly-Typed Mode)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        // 1. 创建合法的星历表空文件夹
        string ephPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!Directory.Exists(ephPath)) {
            Directory.CreateDirectory(ephPath);
        }

        // 2. 直接使用强类型实例化星历引擎！(彻底告别危险的反射与空指针)
        IEphemerides eph = new SwissEphemerides(ephPath);
        
        // 3. 强制指定为 Moshier 模式，纯数学计算，完美解决服务器没实体文件的问题
        var mode = EphCalculationMode.Moshier;
        
        // 4. 解析时间并直接实例化人类图
        // TODO: 跑通连通性测试后，可使用 data.Date 替换此处的硬编码时间
        var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var chart = new HumanDesignChart(parsedDate, eph, mode);

        // 5. 渲染 Blazor 精美图表组件
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        var dictionary = new Dictionary<string, object> { { "Chart", chart } };
        var parameters = ParameterView.FromDictionary(dictionary);
        
        var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);

        return Results.Json(new {
            success = true,
            data = new {
                name = data.Name,
                chartImageSVG = output.ToHtmlString()
            }
        });
    } 
    catch (Exception ex) {
        // 终极报错显影剂
        return Results.Json(new { success = false, message = "底层完整堆栈:\n" + ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
