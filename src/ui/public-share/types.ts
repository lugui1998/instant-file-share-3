export type PublicShareBootstrapPayload = {
  page: PublicSharePageModel
}

export type PublicSharePageKind = 'file' | 'folder' | 'zip'

export type PublicSharePageModel = {
  kind: PublicSharePageKind
  title: string
  description: string
  canonicalUrl: string
  siteName: string
  primaryActionLabel?: string | null
  primaryActionUrl?: string | null
  file?: PublicShareFileModel | null
  folder?: PublicShareFolderModel | null
  zip?: PublicShareZipModel | null
}

export type PublicShareFileModel = {
  fileName: string
  displaySize: string
  preferInline: boolean
  actionVerb: string
  actionLabel: string
}

export type PublicShareFolderModel = {
  name: string
  relativePath: string
  canDownloadAll: boolean
  downloadAllUrl?: string | null
  breadcrumbs: PublicShareBreadcrumb[]
  entries: PublicShareFolderEntryModel[]
  isEmpty: boolean
}

export type PublicShareZipModel = {
  fileName: string
  actionLabel: string
}

export type PublicShareBreadcrumb = {
  label: string
  href: string
}

export type PublicShareFolderEntryModel = {
  name: string
  href: string
  isDirectory: boolean
  modifiedAtLabel: string
  sizeLabel?: string | null
  isParentDirectory: boolean
}
