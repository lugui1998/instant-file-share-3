import fs from 'node:fs/promises'
import path from 'node:path'

const distIndexPath = path.resolve('dist', 'index.html')
const source = await fs.readFile(distIndexPath, 'utf8')

const patched = source
  .replaceAll('src="/assets/', 'src="./assets/')
  .replaceAll("src='/assets/", "src='./assets/")
  .replaceAll('href="/assets/', 'href="./assets/')
  .replaceAll("href='/assets/", "href='./assets/")

if (patched !== source) {
  await fs.writeFile(distIndexPath, patched, 'utf8')
}
