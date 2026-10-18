// ============================================================================
//  File: nativeBridgeAdapter.js
//  Project: BlueRandom (Vue 3 WebUI Frontend)
//  Description:
//    WebView2 原生宿主对象适配桥接层 (Native Bridge Adapter)。
//
//  Architecture & Implementation Highlights:
//    1. 宿主对象契约封装：
//       - 将 C# 暴露的 window.chrome.webview.hostObjects.nativeBridge 转换为前端
//         经典 Electron 时代依赖的 window.configPanelApi 与 window.logApi；
//    2. 字段形态自适应与多态兼容：
//       - 统一将 C# 端可能产生的 PascalCase 字段抹平映射为 JavaScript 前端标准的 camelCase；
//       - 兼容班级管理对象的两种入参形式（直接字符串 vs { id, name } 对象结构）；
//    3. 纯前端开发模式无缝降级：
//       - 当在无 C# 宿主的纯浏览器 (Vite dev server) 环境中运行时，openUrl 降级为 window.open，
//         checkUpdate 降级为直接 fetch GitHub REST API，保障前端独立开发调试体验。
// ============================================================================

import yaml from 'js-yaml'

/**
 * 初始化并挂载原生通信适配器到全局 window 对象上。
 */
export function setupNativeBridgeAdapter() {
  const getBridge = () => window.chrome?.webview?.hostObjects?.nativeBridge

  // 1. 日志适配器
  window.logApi = {
    /**
     * 向 C# 宿主转发 WebUI 控制台日志。
     * @param {string} level - 日志级别 ('info' | 'warn' | 'error')
     * @param {any} text - 日志内容
     */
    send: (level, text) => {
      try {
        const b = getBridge()
        if (b) b.Log(level, String(text || ''))
      } catch (e) {
        console.error('[Bridge] logApi 失败', e)
      }
    }
  }

  // 2. 配置面板 API 适配器
  window.configPanelApi = {
    /**
     * 获取全局配置 YAML 文本并解析为 JavaScript 对象。
     * @returns {Promise<object|null>} 配置对象
     */
    getConfig: async () => {
      try {
        const b = getBridge()
        if (!b) return null
        const yamlText = await b.GetConfigYaml()
        if (!yamlText) return null
        return yaml.load(yamlText)
      } catch (e) {
        console.error('[Bridge] getConfig 失败', e)
        return null
      }
    },

    /**
     * 将配置对象序列化为 YAML 文本并持久化至磁盘。
     * @param {object} configObj - 待保存的完整配置对象
     * @returns {Promise<{ok: boolean, message: string}>} 操作结果
     */
    saveConfig: async (configObj) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        const yamlText = yaml.dump(configObj, { lineWidth: -1 })
        const res = await b.SaveConfigYaml(yamlText)
        if (res === true || res?.ok === true) {
          return { ok: true, message: '配置已保存' }
        }
        return { ok: false, message: (res && res.message) || '保存配置失败' }
      } catch (e) {
        console.error('[Bridge] saveConfig 失败', e)
        return { ok: false, message: e.message }
      }
    },

    /**
     * 请求隐藏配置面板窗口（返回后台常驻或托盘）。
     * @param {boolean} [_saved] - 是否已保存标识
     */
    close: async (_saved) => {
      try {
        const b = getBridge()
        if (b) await b.CloseWindow()
      } catch (e) {
        console.error('[Bridge] close 失败', e)
      }
    },

    /**
     * 获取宿主环境版本号、特权状态、路径等元数据。
     * @returns {Promise<object>}
     */
    getAppInfo: async () => {
      try {
        const b = getBridge()
        if (!b) return {}
        const json = await b.GetAppInfoJson()
        return JSON.parse(json || '{}')
      } catch (e) {
        console.error('[Bridge] getAppInfo 失败', e)
        return {}
      }
    },

    /**
     * 在文件管理器中定位并打开 config.yaml 所在文件夹。
     */
    openConfigFile: async () => {
      try {
        const b = getBridge()
        if (b) await b.OpenConfigFolder()
      } catch (e) {
        console.error('[Bridge] openConfigFile 失败', e)
      }
    },

    /**
     * 打开用户配置目录 (%USERPROFILE%/.blue random/)。
     */
    openConfigDir: async () => {
      try {
        const b = getBridge()
        if (b) await b.OpenConfigFolder()
      } catch (e) {
        console.error('[Bridge] openConfigDir 失败', e)
      }
    },

    /**
     * 安全调起外部系统默认浏览器打开链接。
     * @param {string} url - 目标 URL
     */
    openUrl: async (url) => {
      try {
        const b = getBridge()
        if (b) await b.OpenUrl(String(url || ''))
        else window.open(url, '_blank')
      } catch (e) {
        console.error('[Bridge] openUrl 失败', e)
        window.open(url, '_blank')
      }
    },

    /**
     * 清理全内存素材缓存与 WebView2 会话缓存。
     * @returns {Promise<{ok: boolean, message: string}>}
     */
    clearCache: async () => {
      try {
        const b = getBridge()
        if (b) await b.ClearCache()
        return { ok: true, message: '缓存清理成功' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    /**
     * 重置配置为默认出厂设置并重新加载。
     * @returns {Promise<{ok: boolean, message: string}>}
     */
    resetConfig: async () => {
      try {
        const b = getBridge()
        if (b) await b.ResetConfig()
        return { ok: true, message: '已重置配置' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    /**
     * 创建或更新开机自启 Windows 计划任务。
     * @param {{taskName?: string, exePath?: string, admin?: boolean}} payload
     * @returns {Promise<{ok: boolean, message: string}>}
     */
    createStartupTask: async (payload) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        const { taskName, exePath, admin } = payload || {}
        const ok = await b.CreateStartupTask(taskName || 'Blue Random (Admin)', exePath || '', !!admin)
        return { ok: !!ok, message: ok ? '计划任务已创建/更新' : '创建计划任务失败' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    /**
     * 请求以管理员特权 (runas) 重启自身进程。
     */
    adminElevate: async () => {
      try {
        const b = getBridge()
        if (b) await b.AdminElevate()
      } catch (e) {
        console.error('[Bridge] adminElevate 失败', e)
      }
    },

    /**
     * 重启应用程序。
     */
    restart: async () => {
      try {
        const b = getBridge()
        if (b) await b.Restart()
      } catch (e) {
        console.error('[Bridge] restart 失败', e)
      }
    },

    /**
     * 打开 WebView2 Chromium F12 开发者调试窗口。
     * @param {any} [_target]
     */
    openDevTools: async (_target) => {
      try {
        const b = getBridge()
        if (b) await b.OpenDevTools()
      } catch (e) {
        console.error('[Bridge] openDevTools 失败', e)
      }
    },

    /**
     * 获取当前悬浮球物理坐标。
     * @returns {Promise<{x: number, y: number}|null>}
     */
    getFloatingPosition: async () => {
      try {
        const b = getBridge()
        if (!b) return null
        const json = await b.GetFloatingPositionJson()
        return JSON.parse(json || 'null')
      } catch (e) {
        return null
      }
    },

    /**
     * 获取最近的运行日志条目。
     * @param {number} [maxLines=500] - 最大获取日志行数
     * @returns {Promise<Array<{id: string, time: string, level: string, text: string}>>}
     */
    getLogs: async (maxLines = 500) => {
      try {
        const b = getBridge()
        if (!b) return []
        const json = await b.GetLogsJson(maxLines)
        return JSON.parse(json || '[]')
      } catch (e) {
        return []
      }
    },

    /**
     * 调起系统原生文件选择对话框选择 .exe 可执行文件。
     * @returns {Promise<string>} 选中的 exe 绝对路径
     */
    pickExeFile: async () => {
      try {
        const b = getBridge()
        if (!b) return ''
        return await b.PickFile('可执行程序 (*.exe)|*.exe')
      } catch (e) {
        console.error('[Bridge] pickExeFile 失败', e)
        return ''
      }
    },

    // ===== 班级管理直连 =====
    /**
     * 查询所有已保存班级列表以及当前激活班级 ID。
     * @returns {Promise<{ok: boolean, classes: Array, activeClassId?: string, message?: string}>}
     */
    listClasses: async () => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, classes: [] }
        const json = await b.ListClassesJson()
        const parsed = typeof json === 'string' ? JSON.parse(json || '{}') : json
        let rawList = []
        let activeClassId = ''
        if (Array.isArray(parsed)) {
          rawList = parsed
        } else if (parsed && Array.isArray(parsed.classes)) {
          rawList = parsed.classes
          activeClassId = parsed.activeClassId || ''
        }

        // 统一字段规范化为 camelCase
        const classes = rawList.map(c => ({
          classId: c.classId || c.ClassId || '',
          name: c.name || c.Name || '未命名班级',
          studentList: Array.isArray(c.studentList || c.StudentList)
            ? (c.studentList || c.StudentList).map(s => ({
                name: s.name || s.Name || '',
                weight: typeof s.weight === 'number' ? s.weight : (typeof s.Weight === 'number' ? s.Weight : 1.0),
                letterColor: s.letterColor || s.LetterColor || 'rainbow'
              }))
            : []
        }))

        return { ok: true, classes, activeClassId }
      } catch (e) {
        console.error('[Bridge] listClasses 失败', e)
        return { ok: false, classes: [], message: e.message }
      }
    },

    /**
     * 创建新班级并返回标准化班级数据。
     * @param {string|{name: string}} arg - 班级名称或包含 name 的对象
     * @returns {Promise<{ok: boolean, class?: object, message?: string}>}
     */
    createClass: async (arg) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        const name = typeof arg === 'string' ? arg : (arg?.name || '')
        const json = await b.CreateClass(name)
        const parsed = typeof json === 'string' ? JSON.parse(json || '{}') : json
        const rawClass = parsed?.class || parsed?.Class || (parsed?.classId || parsed?.ClassId ? parsed : null)
        if (rawClass) {
          const cls = {
            classId: rawClass.classId || rawClass.ClassId || '',
            name: rawClass.name || rawClass.Name || name,
            studentList: Array.isArray(rawClass.studentList || rawClass.StudentList)
              ? (rawClass.studentList || rawClass.StudentList).map(s => ({
                  name: s.name || s.Name || '',
                  weight: typeof s.weight === 'number' ? s.weight : (typeof s.Weight === 'number' ? s.Weight : 1.0),
                  letterColor: s.letterColor || s.LetterColor || 'rainbow'
                }))
              : []
          }
          return { ok: true, class: cls }
        }
        return { ok: false, message: '创建班级失败' }
      } catch (e) {
        console.error('[Bridge] createClass 失败', e)
        return { ok: false, message: e.message }
      }
    },

    /**
     * 重命名指定班级。
     * @param {string|{classId: string, name: string}} arg1 - 班级 ID 或配置对象
     * @param {string} [arg2] - 新班级名称
     * @returns {Promise<{ok: boolean, message?: string}>}
     */
    renameClass: async (arg1, arg2) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        let classId = ''
        let name = ''
        if (typeof arg1 === 'object' && arg1 !== null) {
          classId = arg1.classId || arg1.id || ''
          name = arg1.name || ''
        } else {
          classId = String(arg1 || '')
          name = String(arg2 || '')
        }
        const ok = await b.RenameClass(classId, name)
        return { ok: !!ok, message: ok ? '' : '重命名班级失败' }
      } catch (e) {
        console.error('[Bridge] renameClass 失败', e)
        return { ok: false, message: e.message }
      }
    },

    /**
     * 删除指定班级。
     * @param {string|{classId: string}} arg - 班级 ID 或对象
     * @returns {Promise<{ok: boolean, message?: string}>}
     */
    deleteClass: async (arg) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        const classId = typeof arg === 'object' && arg !== null ? (arg.classId || arg.id || '') : String(arg || '')
        const ok = await b.DeleteClass(classId)
        return { ok: !!ok, message: ok ? '' : '删除班级失败' }
      } catch (e) {
        console.error('[Bridge] deleteClass 失败', e)
        return { ok: false, message: e.message }
      }
    },

    /**
     * 读取指定班级的完整学生花名册与权重配置。
     * @param {string|{classId: string}} arg - 班级 ID 或对象
     * @returns {Promise<object|null>}
     */
    loadClass: async (arg) => {
      try {
        const b = getBridge()
        if (!b) return null
        const classId = typeof arg === 'object' && arg !== null ? (arg.classId || arg.id || '') : String(arg || '')
        const json = await b.LoadClassJson(classId)
        if (!json || json === 'null') return null
        const c = JSON.parse(json)
        return {
          classId: c.classId || c.ClassId || '',
          name: c.name || c.Name || '',
          studentList: Array.isArray(c.studentList || c.StudentList)
            ? (c.studentList || c.StudentList).map(s => ({
                name: s.name || s.Name || '',
                weight: typeof s.weight === 'number' ? s.weight : 1.0,
                letterColor: s.letterColor || s.LetterColor || 'rainbow'
              }))
            : []
        }
      } catch (e) {
        return null
      }
    },

    /**
     * 持久化班级学生花名册。
     * @param {object} cls - 班级配置对象
     * @returns {Promise<{ok: boolean, message?: string}>}
     */
    saveClass: async (cls) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false }
        const ok = await b.SaveClassJson(JSON.stringify(cls))
        return { ok: !!ok }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    // ===== 自定义素材直连 =====
    /**
     * 保存自定义多媒体资源文件（音效、图片等）。
     * @param {{fileName: string, mimeType: string, purpose: string, base64: string, duration?: number}} payload
     * @returns {Promise<{ok: boolean, id?: string, fileName?: string, mimeType?: string, purpose?: string, duration?: number, base64?: string, message?: string}>}
     */
    saveCustom: async (payload) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        const { fileName, mimeType, purpose, base64, duration } = payload || {}
        const json = await b.SaveCustomResource(fileName || '', mimeType || '', purpose || '', base64 || '', duration || 0)
        if (!json || json === 'null') return { ok: false, message: '保存失败' }
        const obj = typeof json === 'string' ? JSON.parse(json) : json
        if (obj && (obj.id || obj.Id)) {
          return {
            ok: true,
            id: obj.id || obj.Id || '',
            fileName: obj.fileName || obj.FileName || '',
            mimeType: obj.mimeType || obj.MimeType || '',
            purpose: obj.purpose || obj.Purpose || '',
            duration: obj.duration ?? obj.Duration ?? 0,
            base64: obj.base64 || obj.Base64 || ''
          }
        }
        return { ok: false, message: '解析资源保存结果失败' }
      } catch (e) {
        console.error('[Bridge] saveCustom 失败', e)
        return { ok: false, message: e.message }
      }
    },

    /**
     * 暂存自定义素材（同 saveCustom）。
     * @param {object} payload
     */
    saveCustomStaged: async (payload) => {
      return window.configPanelApi.saveCustom(payload)
    },

    /**
     * 获取指定 ID 的自定义素材元数据及 Base64 编码。
     * @param {string|{id: string}} arg
     * @returns {Promise<object|null>}
     */
    getCustom: async (arg) => {
      try {
        const b = getBridge()
        if (!b) return null
        let id = ''
        if (typeof arg === 'string') {
          id = arg
        } else if (arg && typeof arg === 'object') {
          id = arg.id || arg.customId || ''
        }
        if (!id) return null
        const json = await b.GetCustomResourceJson(id)
        if (!json || json === 'null') return null
        const obj = typeof json === 'string' ? JSON.parse(json) : json
        if (obj) {
          return {
            id: obj.id || obj.Id || '',
            fileName: obj.fileName || obj.FileName || '',
            mimeType: obj.mimeType || obj.MimeType || '',
            purpose: obj.purpose || obj.Purpose || '',
            duration: obj.duration ?? obj.Duration ?? 0,
            base64: obj.base64 || obj.Base64 || ''
          }
        }
        return null
      } catch (e) {
        console.error('[Bridge] getCustom 失败', e)
        return null
      }
    },

    /**
     * 删除指定 ID 的自定义素材。
     * @param {string|{id: string}} arg
     * @returns {Promise<{ok: boolean, message?: string}>}
     */
    deleteCustom: async (arg) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false }
        const id = typeof arg === 'string' ? arg : (arg?.id || arg?.customId || '')
        const ok = await b.DeleteCustomResource(id)
        return { ok: !!ok }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    // ===== 密码与安全 =====
    /**
     * 查询是否启用了安全密码保护。
     * @returns {Promise<{enabled: boolean}>}
     */
    getSecurityStatus: async () => {
      try {
        const b = getBridge()
        if (!b) return { enabled: false }
        const enabled = await b.IsSecurityEnabled()
        return { enabled }
      } catch (e) {
        return { enabled: false }
      }
    },

    /**
     * 校验明文密码是否正确。
     * @param {string|{password: string}} arg
     * @returns {Promise<{ok: boolean, message?: string}>}
     */
    verifyPassword: async (arg) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        const pwd = typeof arg === 'string' ? arg : (arg?.password || '')
        const ok = await b.VerifyPassword(pwd)
        return { ok: !!ok, message: ok ? '' : '密码错误' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    /**
     * 设置并持久化新密码。
     * @param {string|{password: string}} arg
     * @returns {Promise<{ok: boolean, message?: string}>}
     */
    setPassword: async (arg) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        const pwd = typeof arg === 'string' ? arg : (arg?.password || '')
        const ok = await b.SetPassword(pwd)
        return { ok: !!ok, message: ok ? '' : '设置密码失败' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    /**
     * 验证并取消密码保护。
     * @param {string|{password: string}} arg
     * @returns {Promise<{ok: boolean, message?: string}>}
     */
    disablePassword: async (arg) => {
      try {
        const b = getBridge()
        if (!b) return { ok: false, message: 'Bridge unavailable' }
        const pwd = typeof arg === 'string' ? arg : (arg?.password || '')
        const ok = await b.DisablePassword(pwd)
        return { ok: !!ok, message: ok ? '' : '密码错误或取消失败' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    /**
     * 全量重置所有配置项。
     * @returns {Promise<{ok: boolean, message?: string}>}
     */
    resetAllConfig: async () => {
      try {
        const b = getBridge()
        if (!b) return { ok: false }
        const ok = await b.ResetAllConfig()
        return { ok: !!ok, message: ok ? '已全量清除' : '重置失败' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    /**
     * 检查云端 GitHub 最新 Release 更新状态。
     * @returns {Promise<{ok: boolean, status: string, title: string, detail: string, releaseUrl: string}>}
     */
    checkUpdate: async () => {
      try {
        const b = getBridge()
        if (b && typeof b.CheckUpdateJson === 'function') {
          const json = await b.CheckUpdateJson()
          return JSON.parse(json || '{}')
        }
      } catch (e) {
        console.error('[Bridge] checkUpdate 失败', e)
        return {
          ok: false,
          status: 'error',
          title: '检查更新失败',
          detail: '网络请求异常: ' + (e.message || '未知错误'),
          releaseUrl: 'https://github.com/Yun-Hydrogen/blue-random/releases/latest'
        }
      }

      // 纯前端开发模式（无 C# 宿主）时的兜底逻辑
      try {
        const resp = await fetch('https://api.github.com/repos/Yun-Hydrogen/blue-random/releases/latest', {
          headers: { 'Accept': 'application/vnd.github+json' }
        })
        if (resp.ok) {
          const data = await resp.json()
          return {
            ok: true,
            status: 'ok',
            title: `云端最新版本：${data.tag_name || ''}`,
            detail: data.body || '已获取最新发布信息',
            releaseUrl: data.html_url || 'https://github.com/Yun-Hydrogen/blue-random/releases/latest'
          }
        }
      } catch (_) { }

      return {
        ok: false,
        status: 'error',
        title: '检查更新失败',
        detail: '无法连接到更新服务',
        releaseUrl: 'https://github.com/Yun-Hydrogen/blue-random/releases/latest'
      }
    },

    /**
     * 读取本地 changelog.md 文本内容。
     * @returns {Promise<string>} changelog 文本
     */
    getChangelog: async () => {
      try {
        const b = getBridge()
        if (b && typeof b.GetChangelog === 'function') {
          return await b.GetChangelog()
        }
      } catch (e) {
        console.error('[Bridge] getChangelog 失败', e)
      }
    }
  }

  // 3. 结果浮窗 API 适配器 (面向透明 WebView2 浮窗，带消息队列缓冲)
  let pendingOpenPayload = window.__pendingPickResult || null
  let openListener = null
  let resetListener = null

  window.__onPickResultOpen = (payload) => {
    if (typeof openListener === 'function') {
      openListener(payload)
    } else {
      pendingOpenPayload = payload
    }
  }

  window.__onPickResultReset = () => {
    pendingOpenPayload = null
    if (typeof resetListener === 'function') {
      resetListener()
    }
  }

  window.pickResultApi = {
    getConfig: async () => {
      try {
        const b = getBridge()
        if (b && typeof b.GetPickResultConfigJson === 'function') {
          const json = await b.GetPickResultConfigJson()
          return json ? JSON.parse(json) : null
        }
      } catch (e) {
        console.error('[Bridge] pickResultApi.getConfig 失败', e)
      }
      return {
        defaultPlayGachaSound: true,
        soundVolume: 80,
        playMusic: false,
        musicVolume: 60,
        panelOpacity: 0.92,
        panelBgColor: '#ffffff',
        panelBorderColor: '#66ccff'
      }
    },
    getResults: async () => {
      try {
        const b = getBridge()
        if (b && typeof b.GetLastResultsJson === 'function') {
          const json = await b.GetLastResultsJson()
          return json ? JSON.parse(json) : []
        }
      } catch (e) {
        console.error('[Bridge] pickResultApi.getResults 失败', e)
      }
      return []
    },
    onOpen: (callback) => {
      openListener = callback
      if (pendingOpenPayload) {
        const p = pendingOpenPayload
        pendingOpenPayload = null
        try {
          callback(p)
        } catch (err) {
          console.error('[Bridge] 处理暂存的 pickResultOpen 失败', err)
        }
      }
      return () => {
        if (openListener === callback) {
          openListener = null
        }
      }
    },
    onReset: (callback) => {
      resetListener = callback
      return () => {
        if (resetListener === callback) {
          resetListener = null
        }
      }
    },
    close: () => {
      try {
        const b = getBridge()
        if (b && typeof b.ClosePickResult === 'function') {
          b.ClosePickResult()
        }
      } catch (e) {
        console.error('[Bridge] pickResultApi.close 失败', e)
      }
    }
  }

  console.log('[Bridge] nativeBridgeAdapter 注册就绪')
}
