using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

// 【终极方案】：彻底抛弃反射，使用强类型直接引入，强制编译器打包 DLL！
using SharpAstrology.DataModels;
using SharpAstrology.Interfaces;
using SharpAstrology.Enums;
using SharpAstrology.Ephemerides;
using SharpAstrology.HumanDesign.BlazorComponents;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

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

app.MapGet("/", () => "C# SharpAstrology API is running! (Pure Strong-Typed + Downloader)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        // 1. 确保星历表数据文件夹存在
        string ephPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!Directory.Exists(ephPath)) {
            Directory.CreateDirectory(ephPath);
        }
        
        // 2. 自动化云端星历文件下载器
        string[] ephFiles = { "seas_18.se1", "semo_18.se1", "sepl_18.se1" };
        using (var client = new HttpClient()) {
            foreach (var f in ephFiles) {
                var p = Path.Combine(ephPath, f);
                if (!File.Exists(p)) {
                    try {
                        // 【修复 URL 破坏 Bug】：将字符串拆开，防止被 Markdown 引擎错误识别为超链接
                        string cleanUrl = "https://" + "raw.githubusercontent.com/aloistr/swisseph/master/ephe/" + f;
                        var bytes = await client.GetByteArrayAsync(cleanUrl);
                        File.WriteAllBytes(p, bytes);
                    } catch (Exception ex) {
                        Console.WriteLine($"星历文件 {f} 下载警告: {ex.Message}");
                    }
                }
            }
        }

        // 3. 直接使用强类型实例化星历引擎 (绝不使用反射，防止 DLL 被系统剔除)
        IEphemerides eph = new SwissEphemerides(ephPath);
        
        // 4. 解析时间并实例化人类图 (使用 SwissEph 高精度计算模式)
        // 注意：目前用的是 2000 年的固定测试时间，排盘成功后即可替换为解析 data.Date
        var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var chart = new HumanDesignChart(parsedDate, eph, EphCalculationMode.SwissEph);

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
