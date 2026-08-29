<!--
# App.vue 维护说明

本文总结 [src/renderer/App.vue](src/renderer/App.vue) 的结构与方法，便于后续 AI 维护。

## 模块概览
- 作用：Vue 应用根组件，提供 `<router-view />` 插槽并根据路由切换全局样式。
- 技术：Vue 3 `<script setup>` + `vue-router`。

## 页面结构（Template）
- `<router-view />`：由 `src/renderer/router/index.js` 决定渲染 Floating / PickResult / ConfigPanel。

## 关键逻辑
- 全局强制亮色：setup 阶段一次性设置 `<html data-rizui-theme="light">`，
  所有页面的 riz-ui 组件统一亮色，不受系统深色模式影响。
- `watch(route.path)`：监听路由变化，当路径为 `/config-panel` 时为 `<html>`
  和 `<body>` 添加 `is-config-page` class，其余路径移除。
  - `is-config-page`：启用不透明背景、允许滚动、允许文本选择（配置页）。
  - 无 `is-config-page`：透明背景、禁止溢出、禁止文本选择（悬浮窗/结果页）。

## 全局样式
- `* { box-sizing: border-box; }` 统一盒模型。
- `html, body, #app` 全屏填充，无 margin/padding。
- 非配置页：`background: transparent; overflow: hidden; user-select: none;`。
- 配置页：继承默认背景与滚动行为。

## 维护注意事项
- 新增路由页面时，若其需要与配置页不同的全局样式，需在此处 `watch` 中添加对应 class 控制。
-->
<template>
  <router-view />
</template>

<script setup>
import { useRoute } from 'vue-router'
import { watch } from 'vue'

/* 全局强制亮色主题：riz-ui 组件跟随 <html data-rizui-theme> 属性，
   这里一次性写死 light，所有页面（悬浮窗/结果窗/配置面板）均为亮色，
   不受系统深色模式影响。 */
document.documentElement.setAttribute('data-rizui-theme', 'light')

const route = useRoute()
watch(
  () => route.path,
  (newPath) => {
    if (newPath === '/config-panel') {
      document.body.classList.add('is-config-page')
      document.documentElement.classList.add('is-config-page')
    } else {
      document.body.classList.remove('is-config-page')
      document.documentElement.classList.remove('is-config-page')
    }
  },
  { immediate: true }
)
</script>

<style>
* {
  box-sizing: border-box;
}

html,
body,
#app {
  width: 100%;
  height: 100%;
  margin: 0;
  padding: 0;
  font-family: 'UI', 'Bahnschrift', 'Microsoft YaHei UI', sans-serif;
}

html:not(.is-config-page),
body:not(.is-config-page),
html:not(.is-config-page) > body > #app {
  background: transparent;
  overflow: hidden;
  user-select: none;
}

html.is-config-page,
body.is-config-page,
html.is-config-page > body > #app {
  background: #f4f5f7;
  overflow: auto;
  user-select: text;
}
</style>
