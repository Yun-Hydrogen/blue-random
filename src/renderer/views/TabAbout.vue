<!--
================================================================================
  组件：TabAbout.vue
  所属：配置面板 — 关于应用 Tab
  父组件：ConfigPanel.vue（通过 props 传入应用信息）

================================================================================
  一、功能概述
================================================================================
  展示应用的关于信息，包括 Logo、版本、授权、链接和版权声明。

================================================================================
  二、维护注意事项
================================================================================
  - 修改版权信息时确保与 LICENSE 文件一致
  - 外部链接通过 window.open 或 shell.openExternal 打开

  最后更新：2026-08-29
    - 打包后 Logo 失效：src="/image/BlueRandom.png" 绝对路径在 file:// 下解析失败，
      改用 resolveAssetUrl 按协议拼接完整 URL
================================================================================
-->
<template>
  <div class="tab-page">
    <!-- Logo + 标题 -->
    <div class="about-hero">
      <img :src="resolveAssetUrl('/image/BlueRandom.png')" alt="Blue Random" class="about-logo" />
      <h1 class="about-title">Blue Random | 蔚蓝点名</h1>
      <p class="about-subtitle">Sensei，今天您出彩了吗~</p>
      <p class="about-version">当前版本 v{{ appInfo.version || '-' }}</p>
    </div>

    <!-- 许可与链接 -->
    <rizui_card title="许可信息" desc="项目及使用的第三方软件、美术资源的许可证" icon="fa-solid fa-cubes">
      <div class="about-section">

        <p class="about-item">
          <span class="about-label">界面字体</span>
          南西新圆体 
          <a href="https://opensource.org/license/IPA" target="_blank" rel="noopener">IPA Font License</a>
        </p>

        <p class="about-item">
          <span class="about-label">图标字体</span>
          Font Awesome Free
          <a href="https://fontawesome.com/license/free" target="_blank" rel="noopener">SIL OFL 1.1 + MIT</a>
        </p>

        <p class="about-item">
          <span class="about-label">源码许可</span>
          本软件源码  
          <a href="https://www.gnu.org/licenses/agpl-3.0.en.html#license-text" target="_blank" rel="noopener">GNU Affero General Public License Version 3</a>
        </p>

        <p class="about-item">
          <span class="about-label">项目主页</span>
          <a href="https://github.com/Yun-Hydrogen/blue-random" target="_blank" rel="noopener"><i class="fa-brands fa-github"></i> Blue-Random GitHub</a>
        </p>

        <p class="about-item">
          <span class="about-label">三方组件</span>
          UIAccess 
          <a href="https://github.com/shc0743/RunUIAccess/" target="_blank" rel="noopener"><i class="fa-brands fa-github"></i> Github</a>
        </p>
      </div>
    </rizui_card>

    <rizui_card title="相关链接" desc="本项目涉及的相关资源的链接" icon="fa-solid fa-link">
        <div class="about-section">

            <p class="about-item">
            <span class="about-label">三方组件</span>
            UIAccess 
            <a href="https://github.com/shc0743/RunUIAccess/blob/main/LICENSE" target="_blank" rel="noopener">MIT License</a>
            </p>

            <p class="about-item">
            <span class="about-label">蔚蓝档案</span>
            国服 
            <a href="https://bluearchive-cn.com/" target="_blank" rel="noopener">蔚蓝档案</a>
            </p>
            
            <p class="about-item">
            <span class="about-label">蔚蓝档案</span>
            国际 
            <a href="https://bluearchive.nexon.com/" target="_blank" rel="noopener">Blue Archive</a>
            </p>

        </div>
    </rizui_card>

    <!-- 版权声明 -->
    <rizui_card title="版权声明" desc="相关版权信息及免责声明" icon="fa-solid fa-copyright">
      <div class="about-section">
        <p class="about-item about-copyright">
          Blue Archive 由NEXON Korea Corp. &amp; NEXON GAMES Co.,Ltd. 持有版权并保留所有权利。
          在中国大陆区域，《蔚蓝档案》由 NEXON GAMES Co., Ltd. 和 Shanghai Yostar Co., Ltd. 持有版权并保留所有权利。
        </p>

        <p class="about-item about-copyright">
          《蔚蓝点名》是一款从《蔚蓝档案》和 Blue Archive 获得灵感而开放的非官方的第三方工具，软件源代码与 NEXON Korea Corp.、NEXON GAMES Co., Ltd.、Shanghai Yostar Co., Ltd. （根据发行地区确定）没有任何关联。
        </p>

        <p class="about-item about-copyright">
          《蔚蓝点名》使用了部分来自《蔚蓝档案》和 Blue Archive 的游戏美术和音乐资源（可详见readme），这些资源的版权归 NEXON Korea Corp.、NEXON GAMES Co., Ltd.、Shanghai Yostar Co., Ltd. （根据发行地区确定）所有。
        </p>
      </div>
    </rizui_card>

    <rizui_card title="本项目为蔚蓝档案爱好者创作(Fan-Make)项目，请不要用于商业用途" :title-style="{'text-align':'center'}" :desc-style="{'textAlign':'center'}" desc="Code with ❤️ and AI by YunHydrogenP-云氢P | 惠州一中算法AI社&智能信息社"/>


  </div>
</template>

<script setup>
import { rizui_card } from 'riz-ui'
import { resolveAssetUrl } from '@/utils/asset'

const props = defineProps({ appInfo: Object })
</script>

<style scoped>
/* =================================================================
   0. 页面进入动画
   ================================================================= */
.tab-page {
  animation: slide-in 0.3s cubic-bezier(0.25, 0, 0.25, 1);
}
@keyframes slide-in {
  from { opacity: 0; transform: translateX(24px); }
  to   { opacity: 1; transform: translateX(0); }
}

/* =================================================================
   1. Logo 区域
   ================================================================= */
.about-hero {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 28px 0 18px;
}

.about-logo {
  width: 300px;
  height: 80px;
  object-fit: contain;
  margin-bottom: 12px;
  filter: drop-shadow(0 4px 12px rgba(0,0,0,0.12));
}

.about-title {
  margin: 0;
  font-size: 22px;
  font-weight: 700;
  color: #2c3e50;
  letter-spacing: 0.5px;
}

.about-subtitle {
  margin: 8px 0 0;
  font-size: 14px;
  font-weight: 500;
  color: #889;
  letter-spacing: 1px;
}

.about-version {
  margin: 10px 0 0;
  font-size: 12px;
  color: #aab;
}

/* =================================================================
   2. 关于信息区
   ================================================================= */
.about-section {
  font-size: 12px;
  color: #667;
}

.about-item {
  padding: 3px 0;
  line-height: 1.6;
}

.about-label {
  display: inline-block;
  min-width: 60px;
  font-weight: 600;
  color: #889;
}

.about-item a {
  color: #ff9966;
  text-decoration: none;
  transition: color 0.15s;
}

.about-item a:hover {
  color: #86543b;
}

/* 图标字体与文字间距 */
.about-item a i {
  margin-right: 4px;
}

/* =================================================================
   3. 版权声明
   ================================================================= */
.about-copyright {
  color: #aaa;
  font-size: 11px;
  line-height: 1.8;
}
</style>
