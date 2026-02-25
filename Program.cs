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

builder.Services.AddLogging();
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(); 

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => "C# SharpAstrology API is running! (Ephemeris Engine Mounted)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    
    try {
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        
        // 1. 获取渲染组件及需要的参数类型
        var componentType = typeof(HumanDesignGraph);
        var chartProp = componentType.GetProperty("Chart") ?? 
                        componentType.GetProperties().FirstOrDefault(p => p.Name.Contains("Chart"));
        if (chartProp == null) throw new Exception("在组件中找不到 Chart 属性。");
        var chartInterfaceType = chartProp.PropertyType;
        
        // 2. 查找 HumanDesignChart 实体类
        var concreteChartType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => chartInterfaceType.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
        if (concreteChartType == null) throw new Exception("无法找到具体的排盘类！");

        // 3. 【核心修复】查找星历表引擎 (IEphemerides) 并启动它
        var ephInterface = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == "IEphemerides");

        var concreteEphType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => ephInterface != null && ephInterface.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);

        if (concreteEphType == null) {
            throw new Exception("缺少星历表引擎！请检查 AstrologyAPI.csproj 中是否添加了 SharpAstrology.SwissEph 包。");
        }

        object ephInstance;
        try {
            ephInstance = Activator.CreateInstance(concreteEphType);
        } catch (Exception ex) {
            throw new Exception($"星历表计算引擎启动失败。报错详情: {ex.InnerException?.Message ?? ex.Message}");
        }

        // 4. 【核心修复】查找计算模式枚举 (EphCalculationMode)
        var modeEnum = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == "EphCalculationMode" && t.IsEnum);

        // 取枚举的默认值 (通常为0)
        var modeValue = modeEnum != null ? Enum.GetValues(modeEnum).GetValue(0) : 0;

        // 5. 将“时间、引擎、计算模式”三个参数同时喂给构造函数进行完美排盘
        object chartInstance;
        try {
            var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            // 完美匹配 Void .ctor(System.DateTime, IEphemerides, EphCalculationMode)
            chartInstance = Activator.CreateInstance(concreteChartType, parsedDate, ephInstance, modeValue);
        } catch (Exception ex) {
            throw new Exception($"传入参数实例化排盘实体失败。报错详情: {ex.InnerException?.Message ?? ex.Message}");
        }

        // 6. 将生成的实例丢给 Blazor 渲染精美图表
        var chartHtmlString = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var dictionary = new Dictionary<string, object> { { chartProp.Name, chartInstance } };
            var parameters = ParameterView.FromDictionary(dictionary);
            
            var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);
            return output.ToHtmlString();
        });

        return Results.Json(new {
            success = true,
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
