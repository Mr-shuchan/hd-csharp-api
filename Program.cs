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
using System.Reflection; // 引入反射核心命名空间

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

app.MapGet("/", () => "C# SharpAstrology API is running! (Ultimate StackTrace Active)");

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
            Assembly.Load("SharpAstrology.SwissEph");
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

        // 4. 【终极修复】：无敌反射装载器，破解 Private/Internal 权限限制
        object ephInstance = null;
        string ephPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!System.IO.Directory.Exists(ephPath)) {
            System.IO.Directory.CreateDirectory(ephPath);
        }

        // 获取所有构造函数，无视其是否公开
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var ctors = concreteEphType.GetConstructors(flags);
        if (ctors.Length == 0) throw new Exception($"引擎 {concreteEphType.Name} 没有任何实例构造函数！");

        Exception lastCtorError = null;
        foreach (var ctor in ctors.OrderByDescending(c => c.GetParameters().Length)) {
            try {
                var pInfos = ctor.GetParameters();
                var argsToPass = new object[pInfos.Length];
                
                for (int i = 0; i < pInfos.Length; i++) {
                    if (pInfos[i].ParameterType == typeof(string)) {
                        argsToPass[i] = ephPath;
                    } else if (pInfos[i].HasDefaultValue) {
                        argsToPass[i] = pInfos[i].DefaultValue;
                    } else if (pInfos[i].ParameterType.IsValueType) {
                        argsToPass[i] = Activator.CreateInstance(pInfos[i].ParameterType);
                    } else {
                        argsToPass[i] = null;
                    }
                }
                
                ephInstance = ctor.Invoke(argsToPass);
                break; // 只要有一个构造函数成功被破译并启动，立刻跳出
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

        // 6. 【同步修复】使用相同的无敌反射器来实例化排盘对象
        object chartInstance = null;
        var chartCtors = concreteChartType.GetConstructors(flags);
        Exception chartCtorError = null;
        
        // 连通性测试时间
        var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // 【修改点】：从参数最少的简单构造函数开始尝试，并填补字符串空值
        foreach (var ctor in chartCtors.OrderBy(c => c.GetParameters().Length)) {
            try {
                var pInfos = ctor.GetParameters();
                var argsToPass = new object[pInfos.Length];
                
                for (int i = 0; i < pInfos.Length; i++) {
                    var pt = pInfos[i].ParameterType;
                    if (pt == typeof(DateTime) || pt == typeof(DateTimeOffset)) {
                        argsToPass[i] = parsedDate;
                    } else if (ephInterface != null && ephInterface.IsAssignableFrom(pt)) {
                        argsToPass[i] = ephInstance;
                    } else if (modeEnum != null && pt == modeEnum) {
                        argsToPass[i] = modeValue;
                    } else if (pInfos[i].HasDefaultValue) {
                        argsToPass[i] = pInfos[i].DefaultValue;
                    } else if (pt == typeof(string)) {
                        argsToPass[i] = ""; // 字符串给空串，避免引发内部空指针
                    } else if (pt.IsValueType) {
                        argsToPass[i] = Activator.CreateInstance(pt);
                    } else {
                        argsToPass[i] = null;
                    }
                }
                chartInstance = ctor.Invoke(argsToPass);
                break;
            } catch (Exception ex) {
                chartCtorError = ex;
            }
        }

        if (chartInstance == null) {
            // 【核心修改】：将 InnerException 的完整 ToString 抛出，展示最底层的崩溃行号！
            throw new Exception($"排盘实体实例化失败。\n底层完整堆栈:\n{chartCtorError?.InnerException?.ToString() ?? chartCtorError?.ToString()}");
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
