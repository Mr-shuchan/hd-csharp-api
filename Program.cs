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

app.MapGet("/", () => "C# SharpAstrology API is running! (Ultimate Binder Active)");

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

        // 3. 强制加载星历表引擎程序集
        try {
            System.Reflection.Assembly.Load("SharpAstrology.SwissEph");
        } catch (Exception ex) {
            Console.WriteLine("强制加载程序集失败: " + ex.Message);
        }

        var ephInterface = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == "IEphemerides");

        var concreteEphType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => ephInterface != null && ephInterface.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);

        if (concreteEphType == null) {
            throw new Exception("缺少星历表引擎。请确认 AstrologyAPI.csproj 包含了 SharpAstrology.SwissEph。");
        }

        // 4. 【核心修复】：终极参数解构构造器 (完美兼容带默认值的可选参数)
        object ephInstance = null;
        string ephPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!System.IO.Directory.Exists(ephPath)) {
            System.IO.Directory.CreateDirectory(ephPath);
        }

        var ctors = concreteEphType.GetConstructors();
        if (ctors.Length == 0) throw new Exception($"引擎 {concreteEphType.Name} 没有公开的构造函数！");

        Exception lastCtorError = null;
        foreach (var ctor in ctors) {
            try {
                var pInfos = ctor.GetParameters();
                var argsToPass = new object[pInfos.Length];
                
                for (int i = 0; i < pInfos.Length; i++) {
                    if (pInfos[i].ParameterType == typeof(string)) {
                        argsToPass[i] = ephPath; // 遇到字符串，无脑塞入路径
                    } else if (pInfos[i].HasDefaultValue) {
                        argsToPass[i] = pInfos[i].DefaultValue; // 提取并塞入默认参数
                    } else if (pInfos[i].ParameterType.IsValueType) {
                        argsToPass[i] = Activator.CreateInstance(pInfos[i].ParameterType); // 值类型给默认值
                    } else {
                        argsToPass[i] = null; // 引用类型给 null
                    }
                }
                
                // 使用完美对齐的参数数组直接调用构造函数
                ephInstance = ctor.Invoke(argsToPass);
                break; // 如果成功，立刻跳出循环
            } catch (Exception ex) {
                lastCtorError = ex;
            }
        }

        if (ephInstance == null) {
            throw new Exception($"装载引擎彻底失败。最后一个报错: {lastCtorError?.InnerException?.Message ?? lastCtorError?.Message}");
        }

        // 5. 查找计算模式枚举
        var modeEnum = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == "EphCalculationMode" && t.IsEnum);

        var modeValue = modeEnum != null ? Enum.GetValues(modeEnum).GetValue(0) : 0;

        // 6. 实例化排盘对象
        object chartInstance;
        try {
            var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            chartInstance = Activator.CreateInstance(concreteChartType, parsedDate, ephInstance, modeValue);
        } catch (Exception ex) {
            throw new Exception($"传入参数实例化排盘实体失败。报错详情: {ex.InnerException?.Message ?? ex.Message}");
        }

        // 7. 渲染精美图表
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
