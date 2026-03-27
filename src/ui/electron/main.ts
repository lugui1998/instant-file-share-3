import { app, BrowserWindow, dialog, ipcMain, Menu, type OpenDialogOptions } from 'electron'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const rendererUrl = process.env.VITE_DEV_SERVER_URL ?? 'http://127.0.0.1:5173'
const agentBaseUrl = 'http://127.0.0.1:46430'
let mainWindow: BrowserWindow | null = null

function getRendererEntry() {
  return path.join(app.getAppPath(), 'dist', 'index.html')
}

function getWindowIconPath() {
  return path.join(app.getAppPath(), 'icon.ico')
}

function getInstalledAgentPath() {
  return path.resolve(path.dirname(process.execPath), '..', 'InstantFileShare.Agent.exe')
}

async function isAgentReachable() {
  try {
    const response = await fetch(`${agentBaseUrl}/`)
    return response.ok
  } catch {
    return false
  }
}

async function ensureInstalledAgentRunning() {
  if (!app.isPackaged) {
    return
  }

  if (await isAgentReachable()) {
    return
  }

  const agentPath = getInstalledAgentPath()
  try {
    const child = spawn(agentPath, [], {
      detached: true,
      stdio: 'ignore',
      windowsHide: true,
    })
    child.unref()
  } catch {
    return
  }
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1320,
    height: 880,
    minWidth: 1100,
    minHeight: 760,
    autoHideMenuBar: true,
    backgroundColor: '#101112',
    icon: getWindowIconPath(),
    title: 'Instant File Share',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  })

  if (!app.isPackaged) {
    void mainWindow.loadURL(rendererUrl)
  } else {
    void mainWindow.loadFile(getRendererEntry())
  }
}

app.whenReady().then(() => {
  Menu.setApplicationMenu(null)

  ipcMain.handle('cloudflared:pickExecutable', async () => {
    const targetWindow = BrowserWindow.getFocusedWindow() ?? mainWindow
    const options: OpenDialogOptions = {
      title: 'Select cloudflared.exe',
      properties: ['openFile'],
      filters: [
        { name: 'Executable', extensions: ['exe'] },
        { name: 'All Files', extensions: ['*'] },
      ],
    }

    const result = targetWindow
      ? await dialog.showOpenDialog(targetWindow, options)
      : await dialog.showOpenDialog(options)

    return result.canceled ? null : (result.filePaths[0] ?? null)
  })

  void ensureInstalledAgentRunning().finally(() => {
    createWindow()
  })

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      void ensureInstalledAgentRunning().finally(() => {
        createWindow()
      })
    }
  })
})

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit()
  }
})
