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

app.MapGet("/", () => "C# SharpAstrology API is running! (Memory Loaded Engine)");

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

        // 3. 【核心修复】：打破懒加载，强制将星历表引擎程序集读入内存
        try {
            System.Reflection.Assembly.Load("SharpAstrology.SwissEph");
        } catch (Exception ex) {
            Console.WriteLine("强制加载星历表引擎程序集失败: " + ex.Message);
        }

        var ephInterface = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == "IEphemerides");

        var concreteEphType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => ephInterface != null && ephInterface.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);

        if (concreteEphType == null) {
            throw new Exception("扫描内存失败：仍然缺少星历表引擎具体实现类。请确认 AstrologyAPI.csproj 中是否添加了 SharpAstrology.SwissEph。");
        }

        // 4. 【核心修复】：智能适配带参数的引擎构造函数
        object ephInstance;
        try {
            var ephCtor = concreteEphType.GetConstructors().FirstOrDefault();
            // 如果构造函数要求传一个 string (通常是本地星历数据文件夹的路径)
            if (ephCtor != null && ephCtor.GetParameters().Length == 1 && ephCtor.GetParameters()[0].ParameterType == typeof(string)) {
                ephInstance = Activator.CreateInstance(concreteEphType, ""); // 先传空路径，依赖库可能内嵌了基础数据
            } else {
                ephInstance = Activator.CreateInstance(concreteEphType);
            }
        } catch (Exception ex) {
            throw new Exception($"星历表计算引擎启动失败。报错详情: {ex.InnerException?.Message ?? ex.Message}");
        }

        // 5. 查找计算模式枚举
        var modeEnum = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == "EphCalculationMode" && t.IsEnum);

        var modeValue = modeEnum != null ? Enum.GetValues(modeEnum).GetValue(0) : 0;

        // 6. 实例化排盘对象
        object chartInstance;
        try {
            // 目前依然用测试时间进行渲染连通性测试
            var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            chartInstance = Activator.CreateInstance(concreteChartType, parsedDate, ephInstance, modeValue);
        } catch (Exception ex) {
            throw new Exception($"传入参数实例化排盘实体失败。报错详情: {ex.InnerException?.Message ?? ex.Message}");
        }

        // 7. 将生成的实例丢给 Blazor 渲染精美图表
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
