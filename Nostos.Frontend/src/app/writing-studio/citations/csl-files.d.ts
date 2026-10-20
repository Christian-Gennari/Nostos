// The CSL files a spec bundles as text (esbuild's `text` loader).
declare module '*.csl' {
  const content: string;
  export default content;
}
declare module '*.xml' {
  const content: string;
  export default content;
}
