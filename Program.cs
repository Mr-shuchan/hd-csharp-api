using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components; // 修复 ParameterView 上下文丢失
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using SharpAstrology.HumanDesign.BlazorComponents;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Reflection;
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

app.MapGet("/", () => "C# SharpAstrology API is running! (Auto-Downloader Active)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    
    try {
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        
        var hdGraphType = typeof(HumanDesignGraph);
        var chartProp = hdGraphType.GetProperty("Chart") ?? hdGraphType.GetProperties().FirstOrDefault(p => p.Name.Contains("Chart"));
        if (chartProp == null) throw new Exception("在组件中找不到 Chart 属性。");
        
        // 1. 【核心修复】：自动化云端星历文件下载器
        string ephPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        Directory.CreateDirectory(ephPath);
        
        // 下载人类图必需的日月、行星节点核心文件 (覆盖1800-2399年)
        string[] ephFiles = { "seas_18.se1", "semo_18.se1", "sepl_18.se1" };
        using (var client = new HttpClient()) {
            foreach (var f in ephFiles) {
                var p = Path.Combine(ephPath, f);
                if (!File.Exists(p)) {
                    try {
                        // 从官方仓库直链拉取，Render云端速度极快
                        var bytes = await client.GetByteArrayAsync("[https://raw.githubusercontent.com/aloistr/swisseph/master/ephe/](https://raw.githubusercontent.com/aloistr/swisseph/master/ephe/)" + f);
                        File.WriteAllBytes(p, bytes);
                    } catch (Exception ex) {
                        Console.WriteLine($"星历文件 {f} 下载警告: {ex.Message}");
                    }
                }
            }
        }

        // 2. 反射装载星历表引擎
        try { Assembly.Load("SharpAstrology.SwissEph"); } catch {}
        var ephInterface = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.Name == "IEphemerides");
        var concreteEphType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => ephInterface != null && ephInterface.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
        
        if (concreteEphType == null) throw new Exception("缺少星历表引擎具体类。");

        object ephInstance = null;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        
        foreach (var ctor in concreteEphType.GetConstructors(flags).OrderByDescending(c => c.GetParameters().Length)) {
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
        if (ephInstance == null) throw new Exception("引擎实例化彻底失败。");

        // 3. 反射获取排盘类和计算模式
        var chartInterfaceType = chartProp.PropertyType;
        var concreteChartType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => chartInterfaceType.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
        var modeEnum = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.Name == "EphCalculationMode" && t.IsEnum);

        // 4. 【核心修复】：穷举计算模式防崩溃
        object chartInstance = null;
        Exception lastChartErr = null;
        var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc); // 连通性测试时间
        
        // 提取所有的计算模式，如果没有则提供一个默认值
        var modeValues = modeEnum != null ? Enum.GetValues(modeEnum).Cast<object>().ToList() : new List<object> { 0 };

        // 遍历每一种计算模式，直到有一个成功计算出结果且不报 NullReference 为止
        foreach (var modeVal in modeValues) {
            foreach (var ctor in concreteChartType.GetConstructors(flags).OrderByDescending(c => c.GetParameters().Length)) {
                try {
                    var pInfos = ctor.GetParameters();
                    var argsToPass = new object[pInfos.Length];
                    for (int i = 0; i < pInfos.Length; i++) {
                        var pt = pInfos[i].ParameterType;
                        if (pt == typeof(DateTime) || pt == typeof(DateTimeOffset)) argsToPass[i] = parsedDate;
                        else if (ephInterface != null && ephInterface.IsAssignableFrom(pt)) argsToPass[i] = ephInstance;
                        else if (modeEnum != null && pt == modeEnum) argsToPass[i] = modeVal;
                        else if (pInfos[i].HasDefaultValue) argsToPass[i] = pInfos[i].DefaultValue;
                        else if (pt == typeof(string)) argsToPass[i] = "";
                        else if (pt.IsValueType) argsToPass[i] = Activator.CreateInstance(pt);
                        else argsToPass[i] = null;
                    }
                    chartInstance = ctor.Invoke(argsToPass);
                    if (chartInstance != null) break;
                } catch (Exception ex) {
                    lastChartErr = ex;
                }
            }
            if (chartInstance != null) break;
        }

        if (chartInstance == null) {
            throw new Exception($"排盘引擎内部计算崩溃。\n底层堆栈：\n{lastChartErr?.InnerException?.ToString() ?? lastChartErr?.ToString()}");
        }

        // 5. 渲染精美图表
        var dictionary = new Dictionary<string, object> { { chartProp.Name, chartInstance } };
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
        return Results.Json(new { success = false, message = ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
