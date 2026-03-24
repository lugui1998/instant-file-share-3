/// <reference types="vite/client" />
import type { AgentBridge } from './agentBridge'

declare global {
  interface Window {
    instantFileShare?: AgentBridge
  }
}

export {}
