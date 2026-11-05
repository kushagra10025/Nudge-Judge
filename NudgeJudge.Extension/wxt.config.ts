import { defineConfig } from 'wxt';

/**
 * Public half of the development key. It pins the unpacked Chromium extension's ID to
 * bmfgmoobkfipdpiabahjlafpfehjkjfg, which NudgeJudge's host manifest allows (NativeHostRegistration.ChromiumExtensionIds).
 * Store packages (`npm run zip`, mode "store") leave it out: the stores assign their own ID and refuse a key.
 */
const DEV_KEY =
  'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAhzvrnBfBuEEaH5Dl1o+b27TYGvWW8rXsxaT4L/U0m2sAuHCuuEWY9Jwcn9B/0rQnlhUUWtqEnA6P8JSDYptwtrLZFw8VwrvzrZW/iUfEWbSWJIoSZ08GL+OLWvHEzdtCWtKFw30pzkPIbrr67TlEFXcDB6KhT4FQPIRYCjphHaTJ0TaMmxa1k9KKCDih7AzMFBK6Wtshh9lCERHiOntkPXxmYyG+ExaFk0i4VnMcSDmk6s91DRxP968986TmBzt3DIsgBL7TK8zbRozeW9ma2aEMvZxb9pBLsO8o7mtDDJDu5zZoC4F9hB+2LP39KTQrbnxkdyXjWxT1Ks9Cl3hSrwIDAQAB';

// See docs/browser-extension.md.
export default defineConfig({
  // MV3 everywhere, Firefox included: one permission model (optional host permissions, scripting API).
  manifestVersion: 3,
  zip: {
    artifactTemplate: 'nudgejudge-extension-{{version}}-{{browser}}.zip',
    sourcesTemplate: 'nudgejudge-extension-{{version}}-sources.zip',
  },
  manifest: ({ browser, mode }) => ({
    name: 'NudgeJudge',
    description: 'Send links you click in the browser to NudgeJudge, which opens them where your rules say.',
    homepage_url: 'https://github.com/kushagra10025/Nudge-Judge',
    // Everything here works without access to any site: no "read and change all your data" warning on install.
    permissions: ['nativeMessaging', 'contextMenus', 'storage', 'activeTab', 'scripting'],
    // Asked for from the options page: the modifier-click needs a script on every page, and automatic routing
    // needs to see navigations.
    optional_host_permissions: ['<all_urls>'],
    optional_permissions: ['webNavigation'],
    action: { default_title: 'Send this page to NudgeJudge' },
    commands: {
      'send-page': {
        suggested_key: { default: 'Alt+Shift+N' },
        description: 'Send this page to NudgeJudge',
      },
    },
    ...(browser === 'firefox'
      ? {
          browser_specific_settings: {
            gecko: {
              id: 'extension@nudgejudge',
              strict_min_version: '140.0',
              data_collection_permissions: { required: ['none'] },
            },
          },
        }
      : mode === 'store'
        ? {}
        : { key: DEV_KEY }),
  }),
});
