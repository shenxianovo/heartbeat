import { defineConfig } from 'vitest/config'
import { copyFileSync, cpSync, rmSync } from 'node:fs'

export default defineConfig({
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2022',
    minify: false,
    rollupOptions: {
      input: { background: 'src/background.ts', options: 'options.html' },
      output: { entryFileNames: '[name].js', format: 'es' },
    },
  },
  plugins: [
    {
      name: 'copy-manifest',
      closeBundle() {
        copyFileSync('manifest.json', 'dist/manifest.json')
        rmSync('Package/browser-extension', { recursive: true, force: true })
        cpSync('dist', 'Package/browser-extension', { recursive: true })
      },
    },
  ],
  test: { environment: 'node' },
})
