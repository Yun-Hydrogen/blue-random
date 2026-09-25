import yaml from 'js-yaml'

/**
 * 适配器：将 WebView2 宿主对象 window.chrome.webview.hostObjects.nativeBridge
 * 包装为原有 WebUI 代码所依赖的 window.configPanelApi 和 window.logApi
 */
export function setupNativeBridgeAdapter() {
  const getBridge = () => window.chrome?.webview?.hostObjects?.nativeBridge

  // 1. 日志适配器
  window.logApi = {
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

    close: async (_saved) => {
      try {
        const b = getBridge()
        if (b) await b.CloseWindow()
      } catch (e) {
        console.error('[Bridge] close 失败', e)
      }
    },

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

    openConfigFile: async () => {
      try {
        const b = getBridge()
        if (b) await b.OpenConfigFolder()
      } catch (e) {
        console.error('[Bridge] openConfigFile 失败', e)
      }
    },

    openConfigDir: async () => {
      try {
        const b = getBridge()
        if (b) await b.OpenConfigFolder()
      } catch (e) {
        console.error('[Bridge] openConfigDir 失败', e)
      }
    },

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

    clearCache: async () => {
      try {
        const b = getBridge()
        if (b) await b.ClearCache()
        return { ok: true, message: '缓存清理成功' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

    resetConfig: async () => {
      try {
        const b = getBridge()
        if (b) await b.ResetConfig()
        return { ok: true, message: '已重置配置' }
      } catch (e) {
        return { ok: false, message: e.message }
      }
    },

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

    adminElevate: async () => {
      try {
        const b = getBridge()
        if (b) await b.AdminElevate()
      } catch (e) {
        console.error('[Bridge] adminElevate 失败', e)
      }
    },

    restart: async () => {
      try {
        const b = getBridge()
        if (b) await b.Restart()
      } catch (e) {
        console.error('[Bridge] restart 失败', e)
      }
    },

    openDevTools: async (_target) => {
      try {
        const b = getBridge()
        if (b) await b.OpenDevTools()
      } catch (e) {
        console.error('[Bridge] openDevTools 失败', e)
      }
    },

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

    saveCustomStaged: async (payload) => {
      return window.configPanelApi.saveCustom(payload)
    },

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
    }
  }

  console.log('[Bridge] nativeBridgeAdapter 注册就绪')
}
