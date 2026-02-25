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

app.MapGet("/", () => "C# SharpAstrology API is running! (Engine Path Fixed)");

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

        // 3. 打破懒加载，强制加载星历表引擎程序集
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
            throw new Exception("缺少星历表引擎具体实现类。请确认 AstrologyAPI.csproj 中包含了 SharpAstrology.SwissEph。");
        }

        // 4. 【核心修复】：精准定位带有 string 参数的构造函数，并喂给它一个合法的文件夹路径
        object ephInstance;
        try {
            var stringCtor = concreteEphType.GetConstructor(new[] { typeof(string) });
            if (stringCtor != null) {
                // 在服务器运行时目录下创建一个空的星历表文件夹，防止引擎因路径不存在而报错
                string ephPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
                if (!System.IO.Directory.Exists(ephPath)) {
                    System.IO.Directory.CreateDirectory(ephPath);
                }
                ephInstance = Activator.CreateInstance(concreteEphType, ephPath);
            } else {
                ephInstance = Activator.CreateInstance(concreteEphType);
            }
        } catch (Exception ex) {
            var ctors = concreteEphType.GetConstructors().Select(c => "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            throw new Exception($"星历表计算引擎启动失败。目标引擎: {concreteEphType.Name}。可用构造: {string.Join(" | ", ctors)}。报错详情: {ex.InnerException?.Message ?? ex.Message}");
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
