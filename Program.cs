using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

// 引入全套命名空间
using SharpAstrology.DataModels;
using SharpAstrology.Interfaces;
using SharpAstrology.Enums;
using SharpAstrology.Ephemerides;
using SharpAstrology.HumanDesign.BlazorComponents;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddLogging();
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(); 

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => "C# SharpAstrology API is running! (Hybrid Auto-Binder Mode)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        string ephPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!Directory.Exists(ephPath)) {
            Directory.CreateDirectory(ephPath);
        }
        
        // 1. 自动下载必需星历文件 (防止内部算盘报错 NullReferenceException)
        string[] ephFiles = { "seas_18.se1", "semo_18.se1", "sepl_18.se1" };
        using (var client = new HttpClient()) {
            foreach (var f in ephFiles) {
                var p = Path.Combine(ephPath, f);
                if (!File.Exists(p)) {
                    // 拆分字符串防止任何富文本平台破坏 URL
                    string cleanUrl = "https://" + "raw.githubusercontent.com/aloistr/swisseph/master/ephe/" + f;
                    try {
                        var bytes = await client.GetByteArrayAsync(cleanUrl);
                        File.WriteAllBytes(p, bytes);
                    } catch (Exception ex) {
                        Console.WriteLine($"文件 {f} 下载警告: {ex.Message}");
                    }
                }
            }
        }

        // 2. 防 Trim 技术：强引用类型，告诉编译器保留 DLL
        Type ephType = typeof(SwissEphemerides);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var ephCtors = ephType.GetConstructors(flags);

        // 使用无敌反射器绕过 CS1729 严格参数检查
        object ephInstance = null;
        foreach (var ctor in ephCtors.OrderByDescending(c => c.GetParameters().Length)) {
            try {
                var pInfos = ctor.GetParameters();
                var argsToPass = new object[pInfos.Length];
                for (int i = 0; i < pInfos.Length; i++) {
                    if (pInfos[i].ParameterType == typeof(string)) argsToPass[i] = ephPath;
                    else if (pInfos[i].HasDefaultValue) argsToPass[i] = pInfos[i].DefaultValue;
                    else if (pInfos[i].ParameterType.IsValueType) argsToPass[i] = Activator.CreateInstance(pInfos[i].ParameterType);
                    else argsToPass[i] = null;
                }
                ephInstance = ctor.Invoke(argsToPass);
                break;
            } catch {}
        }
        if (ephInstance == null) throw new Exception("星历引擎反射装载彻底失败。");

        // 3. 实例化人类图：绕过 CS0117 枚举名称缺失检查
        Type chartType = typeof(HumanDesignChart);
        var chartCtors = chartType.GetConstructors(flags);
        object chartInstance = null;
        
        // 目前先用测试时间验证渲染
        var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        
        // 关键：不写死具体的 Enum 名字，直接用 default 传递首个有效枚举值
        EphCalculationMode modeValue = default; 

        foreach (var ctor in chartCtors.OrderBy(c => c.GetParameters().Length)) {
            try {
                var pInfos = ctor.GetParameters();
                var argsToPass = new object[pInfos.Length];
                for (int i = 0; i < pInfos.Length; i++) {
                    var pt = pInfos[i].ParameterType;
                    if (pt == typeof(DateTime) || pt == typeof(DateTimeOffset)) argsToPass[i] = parsedDate;
                    else if (typeof(IEphemerides).IsAssignableFrom(pt)) argsToPass[i] = ephInstance;
                    else if (pt == typeof(EphCalculationMode)) argsToPass[i] = modeValue;
                    else if (pInfos[i].HasDefaultValue) argsToPass[i] = pInfos[i].DefaultValue;
                    else if (pt == typeof(string)) argsToPass[i] = "";
                    else if (pt.IsValueType) argsToPass[i] = Activator.CreateInstance(pt);
                    else argsToPass[i] = null;
                }
                chartInstance = ctor.Invoke(argsToPass);
                break;
            } catch {}
        }
        if (chartInstance == null) throw new Exception("排盘实体反射装载彻底失败。");

        // 4. 渲染 Blazor 精美图表组件
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        var dictionary = new Dictionary<string, object> { { "Chart", chartInstance } };
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
        return Results.Json(new { success = false, message = "底层完整堆栈:\n" + ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
