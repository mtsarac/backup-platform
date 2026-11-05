export function validFilePath(path: string, allowRoot = true) {
  return (allowRoot && path === '.') || (path.trim().length > 0 && path.length <= 1024 && !path.startsWith('/') && !/[\\:\p{Cc}]/u.test(path) && path.split('/').every(part => part !== '' && part !== '.' && part !== '..'))
}

export function validFilePaths(paths: readonly string[], required = true, allowRoot = true) {
  return (!required || paths.length > 0) && paths.length <= 256 && paths.every(path => validFilePath(path, allowRoot))
}
