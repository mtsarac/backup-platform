import { useState } from 'react'
import { tx } from '@/lib/i18n'
import { Field } from '@/components/ui/field'
import { inputClass } from '@/components/ui/form-styles'

export function validFilePath(path: string, allowRoot = true) {
  return (allowRoot && path === '.') || (path.trim().length > 0 && path.length <= 1024 && !path.startsWith('/') && !/[\\:\u0000-\u001f\u007f-\u009f]/.test(path) && path.split('/').every(part => part !== '' && part !== '.' && part !== '..'))
}

export function validFilePaths(paths: readonly string[], required = true, allowRoot = true) {
  return (!required || paths.length > 0) && paths.length <= 256 && paths.every(path => validFilePath(path, allowRoot))
}

export function FilePathList({ id, label, paths, onChange, error }: { id: string; label: string; paths: readonly string[]; onChange: (paths: string[]) => void; error?: string }) {
  const [value, setValue] = useState(() => paths.join('\n'))
  return <Field id={id} label={label} error={error} hint={tx('Her satıra bir göreli yol yazın; ayırıcı olarak / kullanın. Mutlak yol ve .. kabul edilmez.')}>
    {control => <textarea {...control} rows={4} className={inputClass} value={value} onChange={event => {
      setValue(event.target.value)
      onChange(event.target.value.split(/\r?\n/).filter(path => path.trim().length > 0))
    }} />}
  </Field>
}
